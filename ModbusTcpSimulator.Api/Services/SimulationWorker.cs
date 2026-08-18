using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModbusTcpSimulator.Core.Conversion;
using ModbusTcpSimulator.Core.Generation;
using ModbusTcpSimulator.Core.Models;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Core.State;

namespace ModbusTcpSimulator.Api.Services;

/// <summary>
/// Core simulation loop.
/// Uses a single 100ms master ticker. Each register entry tracks its own
/// next-due timestamp so we never create a thread per register.
/// When registers are added/changed, ReloadAsync() rebuilds the list safely.
/// </summary>
public sealed class SimulationWorker : BackgroundService
{
    private readonly SimulatorState _state;
    private readonly IRegisterRepository _regRepo;
    private readonly IUnitRepository _unitRepo;
    private readonly ILogger<SimulationWorker> _logger;

    // Each entry = one register config with its generator and next-tick time
    private sealed record RegisterEntry(
        RegisterConfiguration Cfg,
        ValueGenerator Gen,
        byte UnitId)
    {
        public DateTime NextDue { get; set; } = DateTime.UtcNow;
    }

    // Volatile snapshot; replaced atomically on reload
    private volatile List<RegisterEntry> _entries = [];
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    public bool IsRunning { get; private set; }

    public SimulationWorker(
        SimulatorState state,
        IRegisterRepository regRepo,
        IUnitRepository unitRepo,
        ILogger<SimulationWorker> logger)
    {
        _state = state;
        _regRepo = regRepo;
        _unitRepo = unitRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadConfigurationAsync();
        IsRunning = true;
        _logger.LogInformation("SimulationWorker started with {Count} register configs", _entries.Count);

        // Single master loop at 100 ms — checks which entries are due
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                var snapshot = _entries; // atomic read of the reference

                foreach (var entry in snapshot)
                {
                    if (now < entry.NextDue) continue;

                    // Advance next-due
                    entry.NextDue = now.AddMilliseconds(entry.Cfg.UpdateIntervalMs);

                    if (!entry.Cfg.Enabled) continue;

                    try
                    {
                        int regSize = DataTypeConverter.RegisterCount(entry.Cfg.DataType);
                        for (ushort addr = entry.Cfg.StartAddress;
                             addr <= entry.Cfg.EndAddress;
                             addr = (ushort)(addr + Math.Max(1, regSize)))
                        {
                            double value = entry.Gen.Next();
                            var words = DataTypeConverter.Encode(value, entry.Cfg.DataType, entry.Cfg.ByteOrder);
                            _state.SetValue(entry.UnitId, entry.Cfg.RegisterType, addr, words, value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Simulation tick error for config {Id}", entry.Cfg.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }

        IsRunning = false;
    }

    /// <summary>Called after any register configuration change to pick up new/removed registers.</summary>
    public async Task ReloadAsync()
    {
        await _reloadLock.WaitAsync();
        try
        {
            await LoadConfigurationAsync();
            _logger.LogInformation("Simulation reloaded — {Count} configs active", _entries.Count);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task LoadConfigurationAsync()
    {
        var configs = (await _regRepo.GetAllAsync()).ToList();
        var units = (await _unitRepo.GetAllAsync()).ToDictionary(u => u.Id, u => u.UnitId);

        var newEntries = new List<RegisterEntry>();
        foreach (var cfg in configs)
        {
            if (!cfg.Enabled) continue;
            if (!units.TryGetValue(cfg.SimulatedUnitId, out var unitId)) continue;

            // Always create a fresh generator so edits to min/max/strategy take effect immediately
            var gen = new ValueGenerator(cfg);

            newEntries.Add(new RegisterEntry(cfg, gen, unitId)
            {
                NextDue = DateTime.UtcNow
            });
        }

        // Atomic swap
        _entries = newEntries;
    }
}
