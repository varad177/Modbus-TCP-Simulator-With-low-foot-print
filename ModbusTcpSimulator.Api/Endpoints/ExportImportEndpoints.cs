using System.Text.Json;
using System.Text.Json.Serialization;
using ModbusTcpSimulator.Core.Models;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Api.Services;

namespace ModbusTcpSimulator.Api.Endpoints;

public static class ExportImportEndpoints
{
    public static void MapExportImportEndpoints(this WebApplication app)
    {
        // ── Export all configuration ───────────────────────────────────────
        app.MapGet("/api/export", async (
            IUnitRepository unitRepo,
            IRegisterRepository regRepo,
            IAnomalyRepository anomalyRepo) =>
        {
            var units = (await unitRepo.GetAllAsync()).ToList();
            var registers = (await regRepo.GetAllAsync()).ToList();
            var anomalies = (await anomalyRepo.GetAllAsync()).ToList();

            var export = new ExportPayload
            {
                Version = 1,
                ExportedAt = DateTime.UtcNow,
                Units = units,
                Registers = registers,
                Anomalies = anomalies.Select(a =>
                {
                    a.LastTriggered = null; // don't export runtime state
                    return a;
                }).ToList()
            };

            return Results.Json(export, ExportImportJsonOptions.Options);
        });

        // ── Import configuration (merge, skip duplicates) ──────────────────
        app.MapPost("/api/import", async (
            HttpContext ctx,
            IUnitRepository unitRepo,
            IRegisterRepository regRepo,
            IAnomalyRepository anomalyRepo,
            SimulationWorker worker) =>
        {
            ExportPayload? payload;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<ExportPayload>(ExportImportJsonOptions.Options);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON file", detail = ex.Message });
            }

            if (payload == null)
                return Results.BadRequest(new { error = "Empty or invalid import file" });

            int unitsImported = 0, unitsSkipped = 0;
            int regsImported = 0, regsSkipped = 0;
            int anomaliesImported = 0, anomaliesSkipped = 0;

            // ── 1. Import units (skip if UnitId already exists) ──────────
            // Map: old DB Id → new DB Id
            var unitIdMap = new Dictionary<int, int>();

            foreach (var unit in payload.Units)
            {
                var existing = await unitRepo.GetByUnitIdAsync(unit.UnitId);
                if (existing != null)
                {
                    unitIdMap[unit.Id] = existing.Id;
                    unitsSkipped++;
                    continue;
                }

                var newId = await unitRepo.InsertAsync(new SimulatedUnit
                {
                    UnitId = unit.UnitId,
                    Label = unit.Label,
                    Enabled = unit.Enabled,
                    CreatedAt = DateTime.UtcNow
                });
                unitIdMap[unit.Id] = newId;
                unitsImported++;
            }

            // ── 2. Import registers (skip duplicates) ───────────────────
            // Duplicate key: (SimulatedUnitId, RegisterType, StartAddress, EndAddress)
            var existingRegs = (await regRepo.GetAllAsync()).ToList();
            var existingRegKeys = new HashSet<string>(
                existingRegs.Select(r => $"{r.SimulatedUnitId}:{r.RegisterType}:{r.StartAddress}:{r.EndAddress}"));

            foreach (var reg in payload.Registers)
            {
                if (!unitIdMap.TryGetValue(reg.SimulatedUnitId, out var mappedUnitId))
                {
                    regsSkipped++; // unit was skipped and not found
                    continue;
                }

                var key = $"{mappedUnitId}:{reg.RegisterType}:{reg.StartAddress}:{reg.EndAddress}";
                if (existingRegKeys.Contains(key))
                {
                    regsSkipped++;
                    continue;
                }

                await regRepo.InsertAsync(new RegisterConfiguration
                {
                    SimulatedUnitId = mappedUnitId,
                    RegisterType = reg.RegisterType,
                    StartAddress = reg.StartAddress,
                    EndAddress = reg.EndAddress,
                    DataType = reg.DataType,
                    ByteOrder = reg.ByteOrder,
                    Enabled = reg.Enabled,
                    GenerationType = reg.GenerationType,
                    ConstantValue = reg.ConstantValue,
                    MinValue = reg.MinValue,
                    MaxValue = reg.MaxValue,
                    InitialValue = reg.InitialValue,
                    IncrementStep = reg.IncrementStep,
                    SinePeriodSeconds = reg.SinePeriodSeconds,
                    ScatternessType = reg.ScatternessType,
                    ScatternessValue = reg.ScatternessValue,
                    UpdateIntervalMs = reg.UpdateIntervalMs
                });
                regsImported++;
            }

            // ── 3. Import anomalies (skip by Name) ──────────────────────
            var existingAnomalies = (await anomalyRepo.GetAllAsync()).ToList();
            var existingAnomalyNames = new HashSet<string>(
                existingAnomalies.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var anomaly in payload.Anomalies)
            {
                if (!unitIdMap.TryGetValue(anomaly.SimulatedUnitId, out var mappedAnomalyUnitId))
                {
                    anomaliesSkipped++;
                    continue;
                }

                if (existingAnomalyNames.Contains(anomaly.Name))
                {
                    anomaliesSkipped++;
                    continue;
                }

                await anomalyRepo.InsertAsync(new AnomalyConfiguration
                {
                    Name = anomaly.Name,
                    Enabled = anomaly.Enabled,
                    SimulatedUnitId = mappedAnomalyUnitId,
                    RegisterType = anomaly.RegisterType,
                    StartAddress = anomaly.StartAddress,
                    EndAddress = anomaly.EndAddress,
                    Direction = anomaly.Direction,
                    Amount = anomaly.Amount,
                    CustomPerRegister = anomaly.CustomPerRegister,
                    CustomMin = anomaly.CustomMin,
                    CustomMax = anomaly.CustomMax,
                    Pattern = anomaly.Pattern,
                    RecoveryType = anomaly.RecoveryType,
                    DurationSeconds = anomaly.DurationSeconds,
                    TriggerMode = anomaly.TriggerMode,
                    ScheduleIntervalSeconds = anomaly.ScheduleIntervalSeconds,
                    IsScheduleEnabled = anomaly.IsScheduleEnabled
                });
                existingAnomalyNames.Add(anomaly.Name);
                anomaliesImported++;
            }

            // ── Reload simulation worker once ────────────────────────────
            await worker.ReloadAsync();

            return Results.Ok(new
            {
                message = "Import completed",
                units = new { imported = unitsImported, skipped = unitsSkipped },
                registers = new { imported = regsImported, skipped = regsSkipped },
                anomalies = new { imported = anomaliesImported, skipped = anomaliesSkipped }
            });
        });
    }
}

// ── Export payload model ──────────────────────────────────────────────────

public class ExportPayload
{
    public int Version { get; set; }
    public DateTime ExportedAt { get; set; }
    public List<SimulatedUnit> Units { get; set; } = new();
    public List<RegisterConfiguration> Registers { get; set; } = new();
    public List<AnomalyConfiguration> Anomalies { get; set; } = new();
}

// ── Shared JSON options ──────────────────────────────────────────────────

public static class ExportImportJsonOptions
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };
}
