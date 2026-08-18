using Microsoft.Data.Sqlite;
using Dapper;
using ModbusTcpSimulator.Core.Models;

namespace ModbusTcpSimulator.Core.Persistence;

public interface IUnitRepository
{
    Task<IEnumerable<SimulatedUnit>> GetAllAsync();
    Task<SimulatedUnit?> GetByIdAsync(int id);
    Task<SimulatedUnit?> GetByUnitIdAsync(byte unitId);
    Task<int> InsertAsync(SimulatedUnit unit);
    Task UpdateAsync(SimulatedUnit unit);
    Task DeleteAsync(int id);
}

public interface IRegisterRepository
{
    Task<IEnumerable<RegisterConfiguration>> GetAllAsync();
    Task<IEnumerable<RegisterConfiguration>> GetByUnitIdAsync(int simulatedUnitId);
    Task<RegisterConfiguration?> GetByIdAsync(int id);
    Task<int> InsertAsync(RegisterConfiguration reg);
    Task UpdateAsync(RegisterConfiguration reg);
    Task DeleteAsync(int id);
}

public interface IAnomalyRepository
{
    Task<IEnumerable<AnomalyConfiguration>> GetAllAsync();
    Task<AnomalyConfiguration?> GetByIdAsync(int id);
    Task<int> InsertAsync(AnomalyConfiguration anomaly);
    Task UpdateAsync(AnomalyConfiguration anomaly);
    Task DeleteAsync(int id);
    Task UpdateLastTriggeredAsync(int id, DateTime lastTriggered);
}

// ── Implementations ─────────────────────────────────────────────────────────────

public sealed class UnitRepository : IUnitRepository
{
    private readonly string _connectionString;
    public UnitRepository(string connectionString) => _connectionString = connectionString;

    private SqliteConnection Open() => new SqliteConnection(_connectionString);

    public async Task<IEnumerable<SimulatedUnit>> GetAllAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<SimulatedUnit>("SELECT * FROM SimulatedUnits ORDER BY UnitId");
    }

    public async Task<SimulatedUnit?> GetByIdAsync(int id)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<SimulatedUnit>(
            "SELECT * FROM SimulatedUnits WHERE Id = @id", new { id });
    }

    public async Task<SimulatedUnit?> GetByUnitIdAsync(byte unitId)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<SimulatedUnit>(
            "SELECT * FROM SimulatedUnits WHERE UnitId = @unitId", new { unitId });
    }

    public async Task<int> InsertAsync(SimulatedUnit unit)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO SimulatedUnits (UnitId, Label, Enabled, CreatedAt)
              VALUES (@UnitId, @Label, @Enabled, @CreatedAt);
              SELECT last_insert_rowid();", unit);
    }

    public async Task UpdateAsync(SimulatedUnit unit)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE SimulatedUnits SET UnitId=@UnitId, Label=@Label, Enabled=@Enabled WHERE Id=@Id", unit);
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM SimulatedUnits WHERE Id=@id", new { id });
    }
}

public sealed class RegisterRepository : IRegisterRepository
{
    private readonly string _connectionString;
    public RegisterRepository(string connectionString) => _connectionString = connectionString;
    private SqliteConnection Open() => new SqliteConnection(_connectionString);

    public async Task<IEnumerable<RegisterConfiguration>> GetAllAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<RegisterConfiguration>("SELECT * FROM RegisterConfigurations");
    }

    public async Task<IEnumerable<RegisterConfiguration>> GetByUnitIdAsync(int simulatedUnitId)
    {
        using var conn = Open();
        return await conn.QueryAsync<RegisterConfiguration>(
            "SELECT * FROM RegisterConfigurations WHERE SimulatedUnitId=@simulatedUnitId", new { simulatedUnitId });
    }

    public async Task<RegisterConfiguration?> GetByIdAsync(int id)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<RegisterConfiguration>(
            "SELECT * FROM RegisterConfigurations WHERE Id=@id", new { id });
    }

    public async Task<int> InsertAsync(RegisterConfiguration reg)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO RegisterConfigurations
              (SimulatedUnitId, RegisterType, StartAddress, EndAddress, DataType, ByteOrder, Enabled,
               GenerationType, ConstantValue, MinValue, MaxValue, InitialValue, IncrementStep,
               SinePeriodSeconds, ScatternessType, ScatternessValue, UpdateIntervalMs)
              VALUES
              (@SimulatedUnitId, @RegisterType, @StartAddress, @EndAddress, @DataType, @ByteOrder, @Enabled,
               @GenerationType, @ConstantValue, @MinValue, @MaxValue, @InitialValue, @IncrementStep,
               @SinePeriodSeconds, @ScatternessType, @ScatternessValue, @UpdateIntervalMs);
              SELECT last_insert_rowid();", reg);
    }

    public async Task UpdateAsync(RegisterConfiguration reg)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            @"UPDATE RegisterConfigurations SET
              SimulatedUnitId=@SimulatedUnitId, RegisterType=@RegisterType,
              StartAddress=@StartAddress, EndAddress=@EndAddress,
              DataType=@DataType, ByteOrder=@ByteOrder, Enabled=@Enabled,
              GenerationType=@GenerationType, ConstantValue=@ConstantValue,
              MinValue=@MinValue, MaxValue=@MaxValue, InitialValue=@InitialValue,
              IncrementStep=@IncrementStep, SinePeriodSeconds=@SinePeriodSeconds,
              ScatternessType=@ScatternessType, ScatternessValue=@ScatternessValue,
              UpdateIntervalMs=@UpdateIntervalMs
              WHERE Id=@Id", reg);
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM RegisterConfigurations WHERE Id=@id", new { id });
    }
}

public sealed class AnomalyRepository : IAnomalyRepository
{
    private readonly string _connectionString;
    public AnomalyRepository(string connectionString) => _connectionString = connectionString;
    private SqliteConnection Open() => new SqliteConnection(_connectionString);

    public async Task<IEnumerable<AnomalyConfiguration>> GetAllAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<AnomalyConfiguration>("SELECT * FROM AnomalyConfigurations");
    }

    public async Task<AnomalyConfiguration?> GetByIdAsync(int id)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<AnomalyConfiguration>(
            "SELECT * FROM AnomalyConfigurations WHERE Id=@id", new { id });
    }

    public async Task<int> InsertAsync(AnomalyConfiguration anomaly)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO AnomalyConfigurations
              (Name, Enabled, SimulatedUnitId, RegisterType, StartAddress, EndAddress,
               Direction, Amount, CustomPerRegister, CustomMin, CustomMax,
               Pattern, RecoveryType, DurationSeconds, TriggerMode, ScheduleIntervalSeconds, IsScheduleEnabled)
              VALUES
              (@Name, @Enabled, @SimulatedUnitId, @RegisterType, @StartAddress, @EndAddress,
               @Direction, @Amount, @CustomPerRegister, @CustomMin, @CustomMax,
               @Pattern, @RecoveryType, @DurationSeconds, @TriggerMode, @ScheduleIntervalSeconds, @IsScheduleEnabled);
              SELECT last_insert_rowid();", anomaly);
    }

    public async Task UpdateAsync(AnomalyConfiguration anomaly)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            @"UPDATE AnomalyConfigurations SET
              Name=@Name, Enabled=@Enabled, SimulatedUnitId=@SimulatedUnitId,
              RegisterType=@RegisterType, StartAddress=@StartAddress, EndAddress=@EndAddress,
              Direction=@Direction, Amount=@Amount, CustomPerRegister=@CustomPerRegister,
              CustomMin=@CustomMin, CustomMax=@CustomMax, Pattern=@Pattern,
              RecoveryType=@RecoveryType, DurationSeconds=@DurationSeconds,
              TriggerMode=@TriggerMode, ScheduleIntervalSeconds=@ScheduleIntervalSeconds,
              IsScheduleEnabled=@IsScheduleEnabled
              WHERE Id=@Id", anomaly);
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM AnomalyConfigurations WHERE Id=@id", new { id });
    }

    public async Task UpdateLastTriggeredAsync(int id, DateTime lastTriggered)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE AnomalyConfigurations SET LastTriggered=@lastTriggered WHERE Id=@id",
            new { id, lastTriggered });
    }
}
