using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.Data;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Core.State;

namespace ModbusTcpSimulator.Api.Services;

/// <summary>
/// Hosts a real Modbus TCP server.
/// Uses a single TcpListener + NModbus slave network.
/// Each Unit ID in SimulatorState is registered as an independent Modbus slave.
/// </summary>
public sealed class ModbusTcpServerService : BackgroundService
{
    private readonly SimulatorState _state;
    private readonly IUnitRepository _unitRepo;
    private readonly ILogger<ModbusTcpServerService> _logger;
    private readonly string _host;
    private readonly int _port;

    public ModbusTcpServerService(
        SimulatorState state,
        IUnitRepository unitRepo,
        ILogger<ModbusTcpServerService> logger,
        IConfiguration configuration)
    {
        _state = state;
        _unitRepo = unitRepo;
        _logger = logger;
        _host = configuration["Modbus:Host"] ?? "0.0.0.0";
        _port = int.Parse(configuration["Modbus:Port"] ?? "502");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(_host), _port);
        var listener = new TcpListener(endpoint);

        try
        {
            listener.Start();
            _logger.LogInformation("Modbus TCP server listening on {Host}:{Port}", _host, _port);

            var factory = new ModbusFactory();
            var network = factory.CreateSlaveNetwork(listener);

            // Register a slave per unit ID. We use a shared data store proxy so
            // we can update it dynamically as the simulation state changes.
            var units = await _unitRepo.GetAllAsync();
            foreach (var unit in units.Where(u => u.Enabled))
            {
                var dataStore = new SimulatorDataStore(_state, unit.UnitId);
                var slave = factory.CreateSlave(unit.UnitId, dataStore);
                network.AddSlave(slave);
                _logger.LogInformation("Registered Modbus slave Unit ID {UnitId}", unit.UnitId);
            }

            // ListenAsync blocks — stop the listener when cancellation is requested
            using var reg = stoppingToken.Register(() => listener.Stop());
            try { await network.ListenAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* listener was stopped */ }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _logger.LogError("Port {Port} already in use. Modbus TCP server could not start. " +
                "On Windows, port 502 may require elevation. Try port 5020 in configuration.", _port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modbus TCP server error");
        }
        finally
        {
            listener.Stop();
            _logger.LogInformation("Modbus TCP server stopped");
        }
    }

    /// <summary>
    /// Add a new slave unit dynamically (called when user creates a new Unit ID).
    /// Note: NModbus network must be accessible; for simplicity we re-use the live store.
    /// </summary>
    public void RegisterUnit(byte unitId)
    {
        // Will take effect on next restart; dynamic registration requires NModbus internal access.
        _logger.LogInformation("Unit ID {UnitId} registered - will be active on next restart", unitId);
    }
}

/// <summary>
/// NModbus ISlaveDataStore backed by SimulatorState for a specific Unit ID.
/// All reads are served live from the in-memory state.
/// </summary>
internal sealed class SimulatorDataStore : ISlaveDataStore
{
    private readonly SimulatorState _state;
    private readonly byte _unitId;

    public SimulatorDataStore(SimulatorState state, byte unitId)
    {
        _state = state;
        _unitId = unitId;
        CoilDiscretes = new SimulatorPointSource<bool>(state, unitId, ModbusTcpSimulator.Core.Models.RegisterType.Coil);
        CoilInputs = new SimulatorPointSource<bool>(state, unitId, ModbusTcpSimulator.Core.Models.RegisterType.DiscreteInput);
        HoldingRegisters = new SimulatorPointSource<ushort>(state, unitId, ModbusTcpSimulator.Core.Models.RegisterType.HoldingRegister);
        InputRegisters = new SimulatorPointSource<ushort>(state, unitId, ModbusTcpSimulator.Core.Models.RegisterType.InputRegister);
    }

    public IPointSource<bool> CoilDiscretes { get; }
    public IPointSource<bool> CoilInputs { get; }
    public IPointSource<ushort> HoldingRegisters { get; }
    public IPointSource<ushort> InputRegisters { get; }
}

internal sealed class SimulatorPointSource<T> : IPointSource<T>
{
    private readonly SimulatorState _state;
    private readonly byte _unitId;
    private readonly ModbusTcpSimulator.Core.Models.RegisterType _type;

    public SimulatorPointSource(
        SimulatorState state,
        byte unitId,
        ModbusTcpSimulator.Core.Models.RegisterType type)
    {
        _state = state;
        _unitId = unitId;
        _type = type;
    }

    public T[] ReadPoints(ushort startAddress, ushort numberOfPoints)
    {
        var result = new T[numberOfPoints];
        for (int i = 0; i < numberOfPoints; i++)
        {
            var addr = (ushort)(startAddress + i);
            var words = _state.GetWords(_unitId, _type, addr);

            if (typeof(T) == typeof(bool))
                result[i] = (T)(object)(words is { Length: > 0 } && words[0] != 0);
            else if (typeof(T) == typeof(ushort))
                result[i] = (T)(object)(words is { Length: > 0 } ? words[0] : (ushort)0);
        }
        return result;
    }

    public void WritePoints(ushort startAddress, T[] points)
    {
        // Read-only: external writes are ignored
    }
}
