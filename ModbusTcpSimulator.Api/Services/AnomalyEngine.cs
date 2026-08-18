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
/// Manages anomaly lifecycle: scheduling, application, and recovery.
/// 
/// Conflict policy:
///   - Manual/OnDemand anomalies override Scheduled anomalies on the same address.
///   - Last-started wins within the same trigger type.
///   - While an anomaly is active it LOCKS the addresses so SimulationWorker skips them.
///   - On expiry the lock is released and normal simulation resumes immediately.
/// </summary>
public sealed class AnomalyEngine : BackgroundService
{
    private readonly SimulatorState _state;
    private readonly IAnomalyRepository _anomalyRepo;
    private readonly IUnitRepository _unitRepo;
    private readonly IRegisterRepository _regRepo;
    private readonly ILogger<AnomalyEngine> _logger;

    // active anomalies keyed by a string "unitId:type:startAddr-endAddr:anomalyId"
    private readonly ConcurrentDictionary<int, ActiveAnomaly> _activeById = new();
    private readonly ConcurrentDictionary<int, DateTime> _nextScheduled = new();

    public IReadOnlyDictionary<int, ActiveAnomaly> ActiveAnomalies => _activeById;
    public IReadOnlyDictionary<int, DateTime> NextScheduled => _nextScheduled;

    public AnomalyEngine(
        SimulatorState state,
        IAnomalyRepository anomalyRepo,
        IUnitRepository unitRepo,
        IRegisterRepository regRepo,
        ILogger<AnomalyEngine> logger)
    {
        _state = state;
        _anomalyRepo = anomalyRepo;
        _unitRepo = unitRepo;
        _regRepo = regRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadSchedulesAsync();
        _logger.LogInformation("AnomalyEngine started");

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1. Check scheduled anomalies
        var all = await _anomalyRepo.GetAllAsync();
        foreach (var anomaly in all.Where(a => a.Enabled && a.IsScheduleEnabled
                                               && a.TriggerMode == TriggerMode.Scheduled
                                               && a.ScheduleIntervalSeconds > 0))
        {
            if (_nextScheduled.TryGetValue(anomaly.Id, out var nextRun) && now >= nextRun)
            {
                await TriggerInternalAsync(anomaly, isManual: false);
                _nextScheduled[anomaly.Id] = now.AddSeconds(anomaly.ScheduleIntervalSeconds);
                await _anomalyRepo.UpdateLastTriggeredAsync(anomaly.Id, now);
            }
        }

        // 2. Collect expired anomalies first (don't modify dict during foreach)
        //    Skip anomalies already in recovery — they're handled in step 3.5
        var expiredIds = new List<int>();
        foreach (var (id, active) in _activeById)
        {
            if (now >= active.EndsAt && !active.IsRecovering)
                expiredIds.Add(id);
        }

        // 3. Process expired anomalies
        foreach (var id in expiredIds)
        {
            if (!_activeById.TryGetValue(id, out var expired)) continue;

            if (expired.Config.RecoveryType == RecoveryType.Gradual && !expired.IsRecovering)
            {
                // Begin gradual recovery — keep lock, interpolate back to normal
                await BeginRecoveryAsync(expired, now);
                _logger.LogInformation("Anomaly '{Name}' entered gradual recovery", expired.Name);
            }
            else
            {
                // Immediate (or recovery already complete) — unlock and remove
                _activeById.TryRemove(id, out _);
                var unit = await _unitRepo.GetByIdAsync(expired.Config.SimulatedUnitId);
                if (unit != null)
                {
                    _state.UnlockAnomaly(unit.UnitId, expired.Config.RegisterType,
                        expired.Config.StartAddress, expired.Config.EndAddress);
                    await WriteFreshValuesAsync(unit.UnitId, expired.Config, expired.DataType, expired.ByteOrder);
                }
                _logger.LogInformation("Anomaly '{Name}' completed, simulation resumed", expired.Name);
            }
        }

        // 3.5. Process gradual recovery (interpolate back to normal)
        var recoveryCompleteIds = new List<int>();
        foreach (var (id, active) in _activeById)
        {
            if (!active.IsRecovering) continue;

            if (now >= active.RecoveryEndsAt)
            {
                recoveryCompleteIds.Add(id);
                continue;
            }

            // Interpolate from recovery start values toward target fresh values
            double totalMs = (active.RecoveryEndsAt - active.RecoveryStartedAt).TotalMilliseconds;
            double elapsedMs = (now - active.RecoveryStartedAt).TotalMilliseconds;
            double progress = Math.Clamp(elapsedMs / Math.Max(totalMs, 0.001), 0, 1);

            var unit = await _unitRepo.GetByIdAsync(active.Config.SimulatedUnitId);
            if (unit is null) continue;

            var regCfg = active.RecoveryRegConfig;
            if (regCfg is null) continue;
            int stride = Math.Max(1, DataTypeConverter.RegisterCount(active.DataType));

            for (ushort addr = active.Config.StartAddress; addr <= active.Config.EndAddress; addr = (ushort)(addr + stride))
            {
                active.RecoveryStartValues.TryGetValue(addr, out var fromVal);
                active.RecoveryTargetValues.TryGetValue(addr, out var toVal);
                double recovered = fromVal + (toVal - fromVal) * progress;

                if (active.DataType is DataType.Int16 or DataType.UInt16 or DataType.Int32
                    or DataType.UInt32 or DataType.Int64 or DataType.UInt64)
                    recovered = Math.Round(recovered);
                else if (active.DataType is DataType.Bool)
                    recovered = recovered >= 0.5 ? 1.0 : 0.0;

                var encoded = DataTypeConverter.Encode(recovered, active.DataType, active.ByteOrder);
                _state.ForceSetValue(unit.UnitId, active.Config.RegisterType, addr, encoded, recovered);
            }
        }

        // Complete finished recoveries
        foreach (var id in recoveryCompleteIds)
        {
            if (!_activeById.TryRemove(id, out var done)) continue;
            var unit = await _unitRepo.GetByIdAsync(done.Config.SimulatedUnitId);
            if (unit != null)
            {
                _state.UnlockAnomaly(unit.UnitId, done.Config.RegisterType,
                    done.Config.StartAddress, done.Config.EndAddress);
                await WriteFreshValuesAsync(unit.UnitId, done.Config, done.DataType, done.ByteOrder);
            }
            _logger.LogInformation("Anomaly '{Name}' gradual recovery complete, simulation resumed", done.Name);
        }

        // 4. Apply active anomalies (skip recovering ones — handled in 3.5)
        foreach (var (id, active) in _activeById)
        {
            if (active.IsRecovering) continue;
            var unit = await _unitRepo.GetByIdAsync(active.Config.SimulatedUnitId);
            if (unit is null) continue;
            ApplyAnomalyToState(unit.UnitId, active, now);
        }
    }

    /// <summary>Trigger an anomaly manually.</summary>
    public async Task<bool> TriggerManualAsync(int anomalyId)
    {
        var anomaly = await _anomalyRepo.GetByIdAsync(anomalyId);
        if (anomaly is null || !anomaly.Enabled) return false;
        await TriggerInternalAsync(anomaly, isManual: true);
        await _anomalyRepo.UpdateLastTriggeredAsync(anomalyId, DateTime.UtcNow);
        return true;
    }

    private async Task TriggerInternalAsync(AnomalyConfiguration config, bool isManual)
    {
        var unit = await _unitRepo.GetByIdAsync(config.SimulatedUnitId);
        if (unit is null) return;

        var now = DateTime.UtcNow;
        var ends = now.AddSeconds(config.DurationSeconds);

        // Priority: manual > scheduled. If a manual anomaly is already running, skip scheduling.
        if (_activeById.TryGetValue(config.Id, out var existing))
        {
            bool existingIsManual = existing.Config.TriggerMode == TriggerMode.OnDemand;
            if (existingIsManual && !isManual)
            {
                _logger.LogDebug("Skipping scheduled trigger — manual anomaly '{Name}' already active", config.Name);
                return;
            }
            // Release old lock before replacing
            _state.UnlockAnomaly(unit.UnitId, config.RegisterType,
                config.StartAddress, config.EndAddress);
        }

        // Find the matching register config to get the DataType/ByteOrder
        var regs = await _regRepo.GetByUnitIdAsync(config.SimulatedUnitId);
        var matchingReg = regs.FirstOrDefault(r =>
            r.RegisterType == config.RegisterType &&
            r.StartAddress <= config.StartAddress &&
            r.EndAddress >= config.EndAddress);
        var dataType = matchingReg?.DataType ?? DataType.Float32;
        var byteOrder = matchingReg?.ByteOrder ?? ByteOrder.BigEndian;
        var midpoint = matchingReg != null
            ? (matchingReg.MinValue + matchingReg.MaxValue) / 2.0
            : 50.0;

        // Snapshot the current base values from state (before locking)
        var baseValues = SnapshotBaseValues(unit.UnitId, config, dataType, byteOrder);

        // Lock these addresses — SimulationWorker will skip them
        _state.LockForAnomaly(unit.UnitId, config.RegisterType,
            config.StartAddress, config.EndAddress);

        var active = new ActiveAnomaly
        {
            AnomalyId = config.Id,
            Name = config.Name,
            StartedAt = now,
            EndsAt = ends,
            Config = config,
            BaseValues = baseValues,
            MidpointValue = midpoint,
            DataType = dataType,
            ByteOrder = byteOrder
        };

        _activeById[config.Id] = active;

        _logger.LogInformation(
            "Anomaly '{Name}' triggered ({Mode}), addresses {Start}-{End}, duration {Dur}s",
            config.Name, isManual ? "manual" : "scheduled",
            config.StartAddress, config.EndAddress, config.DurationSeconds);
    }

    /// <summary>Snapshot the current live values for the target address range.</summary>
    private Dictionary<ushort, double> SnapshotBaseValues(
        byte unitId, AnomalyConfiguration config, DataType dataType, ByteOrder byteOrder)
    {
        var result = new Dictionary<ushort, double>();
        int stride = Math.Max(1, DataTypeConverter.RegisterCount(dataType));
        for (ushort addr = config.StartAddress; addr <= config.EndAddress; addr = (ushort)(addr + stride))
        {
            var words = _state.GetWords(unitId, config.RegisterType, addr);
            if (words != null)
                result[addr] = DataTypeConverter.Decode(words, dataType, byteOrder);
        }
        return result;
    }

    /// <summary>Transition an active anomaly into gradual recovery mode.</summary>
    private async Task BeginRecoveryAsync(ActiveAnomaly active, DateTime now)
    {
        var regs = await _regRepo.GetByUnitIdAsync(active.Config.SimulatedUnitId);
        var regCfg = regs.FirstOrDefault(r =>
            r.RegisterType == active.Config.RegisterType &&
            r.StartAddress <= active.Config.StartAddress &&
            r.EndAddress >= active.Config.EndAddress);

        // Snapshot current values as the interpolation start point
        var startValues = SnapshotBaseValues(
            (await _unitRepo.GetByIdAsync(active.Config.SimulatedUnitId))!.UnitId,
            active.Config, active.DataType, active.ByteOrder);

        // Generate the target fresh values we're interpolating toward
        var targetValues = new Dictionary<ushort, double>();
        if (regCfg != null)
        {
            var gen = new ValueGenerator(regCfg);
            int stride = Math.Max(1, DataTypeConverter.RegisterCount(active.DataType));
            for (ushort addr = active.Config.StartAddress; addr <= active.Config.EndAddress; addr = (ushort)(addr + stride))
                targetValues[addr] = gen.Next();
        }

        active.IsRecovering = true;
        active.RecoveryStartedAt = now;
        active.RecoveryEndsAt = now.AddSeconds(active.Config.DurationSeconds);
        active.RecoveryStartValues = startValues;
        active.RecoveryTargetValues = targetValues;
        active.RecoveryRegConfig = regCfg;
    }

    /// <summary>Write fresh simulation values to the given address range.</summary>
    private async Task WriteFreshValuesAsync(byte unitId, AnomalyConfiguration config,
        DataType dataType, ByteOrder byteOrder)
    {
        var regs = await _regRepo.GetByUnitIdAsync(config.SimulatedUnitId);
        var regCfg = regs.FirstOrDefault(r =>
            r.RegisterType == config.RegisterType &&
            r.StartAddress <= config.StartAddress &&
            r.EndAddress >= config.EndAddress);
        if (regCfg == null) return;

        var gen = new ValueGenerator(regCfg);
        int stride = Math.Max(1, DataTypeConverter.RegisterCount(dataType));
        for (ushort addr = config.StartAddress; addr <= config.EndAddress; addr = (ushort)(addr + stride))
        {
            var freshValue = gen.Next();
            var encoded = DataTypeConverter.Encode(freshValue, dataType, byteOrder);
            _state.ForceSetValue(unitId, config.RegisterType, addr, encoded, freshValue);
        }
    }

    private void ApplyAnomalyToState(byte unitId, ActiveAnomaly active, DateTime now)
    {
        var cfg = active.Config;
        double totalSeconds = (active.EndsAt - active.StartedAt).TotalSeconds;
        double elapsed = (now - active.StartedAt).TotalSeconds;
        double progress = Math.Clamp(elapsed / Math.Max(totalSeconds, 0.001), 0, 1);
        var rng = new Random(); // per-call, seeded differently for oscillation variety

        var dataType = active.DataType;
        var byteOrder = active.ByteOrder;
        int stride = Math.Max(1, DataTypeConverter.RegisterCount(dataType));

        for (ushort addr = cfg.StartAddress; addr <= cfg.EndAddress; addr = (ushort)(addr + stride))
        {
            // Use midpoint for Increase/Decrease, live value for CustomValue
            double baseValue = cfg.Direction == AnomalyDirection.CustomValue
                ? (active.BaseValues.TryGetValue(addr, out var bv) ? bv : 0)
                : active.MidpointValue;

            double targetValue = cfg.Direction switch
            {
                AnomalyDirection.Increase => baseValue * (1.0 + cfg.Amount / 100.0),
                AnomalyDirection.Decrease => baseValue * (1.0 - cfg.Amount / 100.0),
                AnomalyDirection.CustomValue => cfg.CustomPerRegister
                    ? rng.NextDouble() * (cfg.CustomMax - cfg.CustomMin) + cfg.CustomMin
                    : cfg.Amount,
                _ => baseValue
            };

            double anomalyValue = cfg.Pattern switch
            {
                AnomalyPattern.InstantSpike => targetValue,
                AnomalyPattern.GradualRamp => baseValue + (targetValue - baseValue) * progress,
                AnomalyPattern.Oscillation =>
                    baseValue + (targetValue - baseValue) * Math.Abs(Math.Sin(progress * Math.PI * 4)),
                _ => targetValue
            };

            // Round to integer for integer types and clamp to 0/1 for booleans
            if (dataType is DataType.Int16 or DataType.UInt16 or DataType.Int32
                          or DataType.UInt32 or DataType.Int64 or DataType.UInt64)
            {
                anomalyValue = Math.Round(anomalyValue);
            }
            else if (dataType is DataType.Bool)
            {
                anomalyValue = anomalyValue >= 0.5 ? 1.0 : 0.0;
            }

            var encoded = DataTypeConverter.Encode(anomalyValue, dataType, byteOrder);
            _state.ForceSetValue(unitId, cfg.RegisterType, addr, encoded, anomalyValue);
        }
    }

    private async Task LoadSchedulesAsync()
    {
        _nextScheduled.Clear();
        var anomalies = await _anomalyRepo.GetAllAsync();
        var now = DateTime.UtcNow;
        foreach (var a in anomalies.Where(x => x.Enabled && x.IsScheduleEnabled && x.TriggerMode == TriggerMode.Scheduled))
        {
            var next = a.LastTriggered.HasValue
                ? a.LastTriggered.Value.AddSeconds(a.ScheduleIntervalSeconds)
                : now.AddSeconds(a.ScheduleIntervalSeconds);
            _nextScheduled[a.Id] = next < now ? now.AddSeconds(10) : next;
        }
    }

    public async Task ReloadSchedulesAsync() => await LoadSchedulesAsync();

    /// <summary>Stop an active anomaly manually.</summary>
    public async Task<bool> StopManualAsync(int anomalyId)
    {
        if (!_activeById.TryGetValue(anomalyId, out var stopped))
            return false;

        if (stopped.IsRecovering)
        {
            // Already recovering — let it finish naturally
            return true;
        }

        if (stopped.Config.RecoveryType == RecoveryType.Gradual)
        {
            // Begin gradual recovery instead of instant snap
            await BeginRecoveryAsync(stopped, DateTime.UtcNow);
            _logger.LogInformation("Anomaly '{Name}' stopped manually — entering gradual recovery", stopped.Name);
            return true;
        }

        // Immediate recovery — remove and write fresh values now
        _activeById.TryRemove(anomalyId, out _);

        var unit = await _unitRepo.GetByIdAsync(stopped.Config.SimulatedUnitId);
        if (unit != null)
        {
            // Only unlock if no other active anomaly still covers these addresses
            bool stillCovered = _activeById.Values.Any(other =>
                other.Config.SimulatedUnitId == stopped.Config.SimulatedUnitId &&
                other.Config.RegisterType == stopped.Config.RegisterType &&
                other.Config.StartAddress <= stopped.Config.EndAddress &&
                other.Config.EndAddress >= stopped.Config.StartAddress);

            if (!stillCovered)
            {
                _state.UnlockAnomaly(unit.UnitId, stopped.Config.RegisterType,
                    stopped.Config.StartAddress, stopped.Config.EndAddress);
                await WriteFreshValuesAsync(unit.UnitId, stopped.Config, stopped.DataType, stopped.ByteOrder);
            }
        }

        _logger.LogInformation("Anomaly '{Name}' stopped manually by user request", stopped.Name);
        return true;
    }
}
