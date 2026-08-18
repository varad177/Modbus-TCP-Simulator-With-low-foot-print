using System.Text.Json.Serialization;

namespace ModbusTcpSimulator.Core.Models;

// ── Enumerations ───────────────────────────────────────────────────────────────

public enum RegisterType { Coil, DiscreteInput, HoldingRegister, InputRegister }

public enum DataType
{
    Bool,
    UInt16, Int16,
    UInt32, Int32,
    Float32,
    UInt64, Int64,
    Float64
}

public enum ByteOrder { BigEndian, LittleEndian, WordSwap }

public enum GenerationType { Constant, Random, Increment, Decrement, Sine }

public enum ScatternessType { None, Percentage, Absolute }

public enum AnomalyDirection { Increase, Decrease, CustomValue }

public enum AnomalyPattern { InstantSpike, GradualRamp, Oscillation }

public enum RecoveryType { Immediate, Gradual }

public enum TriggerMode { OnDemand, Scheduled }

// ── Entities ───────────────────────────────────────────────────────────────────

public class SimulatedUnit
{
    public int Id { get; set; }
    public byte UnitId { get; set; }        // Modbus Unit ID 1-247
    public string? Label { get; set; }       // Optional UI label
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RegisterConfiguration
{
    public int Id { get; set; }
    public int SimulatedUnitId { get; set; }
    public RegisterType RegisterType { get; set; }
    public ushort StartAddress { get; set; }
    public ushort EndAddress { get; set; }       // == StartAddress for single register
    public DataType DataType { get; set; }
    public ByteOrder ByteOrder { get; set; } = ByteOrder.BigEndian;
    public bool Enabled { get; set; } = true;

    // Value generation
    public GenerationType GenerationType { get; set; }
    public double ConstantValue { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public double InitialValue { get; set; }
    public double IncrementStep { get; set; } = 1;

    // Sine wave params
    public double SinePeriodSeconds { get; set; } = 60;

    // Scatterness
    public ScatternessType ScatternessType { get; set; }
    public double ScatternessValue { get; set; }  // e.g. 5 = 5% or ±5 absolute

    // Update interval in milliseconds
    public int UpdateIntervalMs { get; set; } = 1000;
}

public class AnomalyConfiguration
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // Target
    public int SimulatedUnitId { get; set; }
    public RegisterType RegisterType { get; set; }
    public ushort StartAddress { get; set; }
    public ushort EndAddress { get; set; }          // == StartAddress for single

    // Anomaly behaviour
    public AnomalyDirection Direction { get; set; }
    public double Amount { get; set; }              // % for Inc/Dec, absolute for Custom
    public bool CustomPerRegister { get; set; }     // if Custom: same or independent per reg
    public double CustomMin { get; set; }
    public double CustomMax { get; set; }
    public AnomalyPattern Pattern { get; set; }
    public RecoveryType RecoveryType { get; set; }
    public int DurationSeconds { get; set; } = 10;

    // Trigger
    public TriggerMode TriggerMode { get; set; }
    public double ScheduleIntervalSeconds { get; set; }  // 0 = on demand
    public bool IsScheduleEnabled { get; set; }

    // Runtime tracking (not persisted)
    public DateTime? LastTriggered { get; set; }
}

// ── Runtime state records ──────────────────────────────────────────────────────

public class ActiveAnomaly
{
    public int AnomalyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime EndsAt { get; set; }
    public AnomalyConfiguration Config { get; set; } = null!;
    public Dictionary<ushort, double> BaseValues { get; set; } = new();
    public double MidpointValue { get; set; }  // (MinValue + MaxValue) / 2 from register config
    public DataType DataType { get; set; } = DataType.Float32;
    public ByteOrder ByteOrder { get; set; } = ByteOrder.BigEndian;
}
