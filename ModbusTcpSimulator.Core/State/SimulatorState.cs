using System.Collections.Concurrent;
using ModbusTcpSimulator.Core.Models;

namespace ModbusTcpSimulator.Core.State;

/// <summary>
/// Thread-safe in-memory store for all simulated register values.
/// Keyed by (unitId, registerType, address) → current raw ushort[] words.
/// </summary>
public sealed class SimulatorState
{
    // Main register store: unit → type → address → raw 16-bit words
    private readonly ConcurrentDictionary<byte, UnitState> _units = new();

    // Track which addresses changed since last broadcast
    private readonly ConcurrentDictionary<RegisterKey, double> _pendingChanges = new();

    // Addresses locked by active anomalies — simulation skips these
    private readonly ConcurrentDictionary<RegisterKey, bool> _anomalyLocks = new();

    public UnitState GetOrAddUnit(byte unitId) =>
        _units.GetOrAdd(unitId, _ => new UnitState());

    public UnitState? GetUnit(byte unitId) =>
        _units.TryGetValue(unitId, out var u) ? u : null;

    public IEnumerable<byte> GetUnitIds() => _units.Keys;

    /// <summary>Write a value — simulation uses this, respects anomaly locks.</summary>
   public bool SetValue(byte unitId, RegisterType type, ushort address, ushort[] words, double logicalValue)
{
    var unit = GetOrAddUnit(unitId);
    for (int i = 0; i < words.Length; i++)
    {
        var addr = (ushort)(address + i);
        if (_anomalyLocks.ContainsKey(new RegisterKey(unitId, type, addr)))
            return false; // some word in this group is anomaly-locked
    }
    unit.SetWords(type, address, words);
    _pendingChanges[new RegisterKey(unitId, type, address)] = logicalValue;
    return true;
}

    /// <summary>Force-write a value regardless of anomaly lock (used by AnomalyEngine).</summary>
    public void ForceSetValue(byte unitId, RegisterType type, ushort address, ushort[] words, double logicalValue)
    {
        var unit = GetOrAddUnit(unitId);
        unit.SetWords(type, address, words);
        var key = new RegisterKey(unitId, type, address);
        _pendingChanges[key] = logicalValue;
    }

    /// <summary>Lock addresses for an anomaly so the simulation skips them.</summary>
    public void LockForAnomaly(byte unitId, RegisterType type, ushort startAddress, ushort endAddress)
    {
        for (ushort addr = startAddress; addr <= endAddress; addr++)
            _anomalyLocks[new RegisterKey(unitId, type, addr)] = true;
    }

    /// <summary>Release anomaly lock — simulation resumes normal generation.</summary>
    public void UnlockAnomaly(byte unitId, RegisterType type, ushort startAddress, ushort endAddress)
    {
        for (ushort addr = startAddress; addr <= endAddress; addr++)
            _anomalyLocks.TryRemove(new RegisterKey(unitId, type, addr), out _);
    }

    public bool IsAnomalyLocked(byte unitId, RegisterType type, ushort address) =>
        _anomalyLocks.ContainsKey(new RegisterKey(unitId, type, address));

    public ushort[]? GetWords(byte unitId, RegisterType type, ushort address)
    {
        var unit = GetUnit(unitId);
        return unit?.GetWords(type, address);
    }

    /// <summary>Drain all pending changes atomically for WebSocket broadcast.</summary>
    public Dictionary<RegisterKey, double> DrainPendingChanges()
    {
        var result = new Dictionary<RegisterKey, double>();
        foreach (var key in _pendingChanges.Keys)
        {
            if (_pendingChanges.TryRemove(key, out var val))
                result[key] = val;
        }
        return result;
    }

    public void RemoveUnit(byte unitId) => _units.TryRemove(unitId, out _);

    /// <summary>Clear all stored registers and pending changes (used when re-indexing configuration).</summary>
    public void ClearAll()
    {
        _units.Clear();
        _pendingChanges.Clear();
    }
}

public sealed class UnitState
{
    // Separate arrays per register type  
    // Using ConcurrentDictionary<address, words[]> for sparse register spaces
    private readonly ConcurrentDictionary<ushort, ushort[]>[] _banks = new ConcurrentDictionary<ushort, ushort[]>[4];

    public UnitState()
    {
        for (int i = 0; i < 4; i++)
            _banks[i] = new ConcurrentDictionary<ushort, ushort[]>();
    }

    private static int BankIndex(RegisterType t) => t switch
    {
        RegisterType.Coil => 0,
        RegisterType.DiscreteInput => 1,
        RegisterType.HoldingRegister => 2,
        RegisterType.InputRegister => 3,
        _ => throw new ArgumentOutOfRangeException()
    };

    public void SetWords(RegisterType type, ushort address, ushort[] words)
{
    var bank = _banks[BankIndex(type)];
    for (int i = 0; i < words.Length; i++)
        bank[(ushort)(address + i)] = new[] { words[i] };
}

    public ushort[]? GetWords(RegisterType type, ushort address) =>
    _banks[BankIndex(type)].TryGetValue(address, out var w) ? w : null;

    // NModbus server data store helpers
    public bool GetCoil(ushort address) =>
        GetWords(RegisterType.Coil, address) is { } w && w[0] != 0;

    public bool GetDiscreteInput(ushort address) =>
        GetWords(RegisterType.DiscreteInput, address) is { } w && w[0] != 0;

    public ushort GetHoldingRegister(ushort address) =>
        GetWords(RegisterType.HoldingRegister, address) is { } w && w.Length > 0 ? w[0] : (ushort)0;

    public ushort GetInputRegister(ushort address) =>
        GetWords(RegisterType.InputRegister, address) is { } w && w.Length > 0 ? w[0] : (ushort)0;
}

public record RegisterKey(byte UnitId, RegisterType RegisterType, ushort Address);
