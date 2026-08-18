using Microsoft.Data.Sqlite;

namespace ModbusTcpSimulator.Core.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource) && builder.DataSource != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS SimulatedUnits (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    UnitId    INTEGER NOT NULL UNIQUE CHECK(UnitId >= 1 AND UnitId <= 247),
    Label     TEXT,
    Enabled   INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS RegisterConfigurations (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    SimulatedUnitId     INTEGER NOT NULL REFERENCES SimulatedUnits(Id) ON DELETE CASCADE,
    RegisterType        INTEGER NOT NULL,
    StartAddress        INTEGER NOT NULL,
    EndAddress          INTEGER NOT NULL,
    DataType            INTEGER NOT NULL DEFAULT 0,
    ByteOrder           INTEGER NOT NULL DEFAULT 0,
    Enabled             INTEGER NOT NULL DEFAULT 1,
    GenerationType      INTEGER NOT NULL DEFAULT 0,
    ConstantValue       REAL    NOT NULL DEFAULT 0,
    MinValue            REAL    NOT NULL DEFAULT 0,
    MaxValue            REAL    NOT NULL DEFAULT 100,
    InitialValue        REAL    NOT NULL DEFAULT 0,
    IncrementStep       REAL    NOT NULL DEFAULT 1,
    SinePeriodSeconds   REAL    NOT NULL DEFAULT 60,
    ScatternessType     INTEGER NOT NULL DEFAULT 0,
    ScatternessValue    REAL    NOT NULL DEFAULT 0,
    UpdateIntervalMs    INTEGER NOT NULL DEFAULT 1000
);

CREATE TABLE IF NOT EXISTS AnomalyConfigurations (
    Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name                    TEXT    NOT NULL,
    Enabled                 INTEGER NOT NULL DEFAULT 1,
    SimulatedUnitId         INTEGER NOT NULL REFERENCES SimulatedUnits(Id) ON DELETE CASCADE,
    RegisterType            INTEGER NOT NULL,
    StartAddress            INTEGER NOT NULL,
    EndAddress              INTEGER NOT NULL,
    Direction               INTEGER NOT NULL DEFAULT 0,
    Amount                  REAL    NOT NULL DEFAULT 10,
    CustomPerRegister       INTEGER NOT NULL DEFAULT 0,
    CustomMin               REAL    NOT NULL DEFAULT 0,
    CustomMax               REAL    NOT NULL DEFAULT 100,
    Pattern                 INTEGER NOT NULL DEFAULT 0,
    RecoveryType            INTEGER NOT NULL DEFAULT 0,
    DurationSeconds         INTEGER NOT NULL DEFAULT 10,
    TriggerMode             INTEGER NOT NULL DEFAULT 0,
    ScheduleIntervalSeconds INTEGER NOT NULL DEFAULT 600,
    IsScheduleEnabled       INTEGER NOT NULL DEFAULT 0,
    LastTriggered           TEXT
);
";
        await cmd.ExecuteNonQueryAsync();
    }
}
