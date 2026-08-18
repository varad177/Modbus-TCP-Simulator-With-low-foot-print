using ModbusTcpSimulator.Core.Models;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Api.Services;

namespace ModbusTcpSimulator.Api.Endpoints;

public static class AnomalyEndpoints
{
    public static void MapAnomalyEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/anomalies");

        grp.MapGet("/", async (IAnomalyRepository repo, AnomalyEngine engine) =>
        {
            var all = (await repo.GetAllAsync()).ToList();
            var active = engine.ActiveAnomalies;
            var next = engine.NextScheduled;
            var result = all.Select(a => new
            {
                a.Id, a.Name, a.Enabled, a.SimulatedUnitId, a.RegisterType,
                a.StartAddress, a.EndAddress, a.Direction, a.Amount, a.Pattern,
                a.RecoveryType, a.DurationSeconds, a.TriggerMode,
                a.ScheduleIntervalSeconds, a.IsScheduleEnabled, a.LastTriggered,
                a.CustomPerRegister, a.CustomMin, a.CustomMax,
                IsActive = active.Values.Any(aa => aa.AnomalyId == a.Id),
                NextScheduled = next.TryGetValue(a.Id, out var ns) ? (DateTime?)ns : null
            });
            return Results.Ok(result);
        });

        grp.MapGet("/{id:int}", async (int id, IAnomalyRepository repo) =>
        {
            var a = await repo.GetByIdAsync(id);
            return a is null ? Results.NotFound() : Results.Ok(a);
        });

        grp.MapPost("/", async (CreateAnomalyRequest req, IAnomalyRepository repo, AnomalyEngine engine) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required" });
            if (req.StartAddress > req.EndAddress)
                return Results.BadRequest(new { error = "StartAddress must be <= EndAddress" });
            if (req.DurationSeconds < 1)
                return Results.BadRequest(new { error = "Duration must be >= 1 second" });

            // Enforce Modbus rules for Coil / DiscreteInput
            var direction = req.Direction;
            var pattern = req.Pattern;
            var customMin = req.CustomMin;
            var customMax = req.CustomMax;
            var amount = req.Amount;

            if (req.RegisterType is RegisterType.Coil or RegisterType.DiscreteInput)
            {
                // Boolean registers: only CustomValue direction with InstantSpike
                if (direction is not AnomalyDirection.CustomValue)
                    return Results.BadRequest(new { error = "Coil/DiscreteInput anomalies must use CustomValue direction (boolean 0/1 only)" });
                if (pattern is not AnomalyPattern.InstantSpike)
                    return Results.BadRequest(new { error = "Coil/DiscreteInput anomalies only support InstantSpike pattern" });
                // Force 0/1 range
                customMin = 0;
                customMax = 1;
            }

            var anomaly = new AnomalyConfiguration
            {
                Name = req.Name,
                Enabled = req.Enabled,
                SimulatedUnitId = req.SimulatedUnitId,
                RegisterType = req.RegisterType,
                StartAddress = req.StartAddress,
                EndAddress = req.EndAddress,
                Direction = direction,
                Amount = amount,
                CustomPerRegister = req.CustomPerRegister,
                CustomMin = customMin,
                CustomMax = customMax,
                Pattern = pattern,
                RecoveryType = req.RecoveryType,
                DurationSeconds = req.DurationSeconds,
                TriggerMode = req.TriggerMode,
                ScheduleIntervalSeconds = req.ScheduleIntervalSeconds,
                IsScheduleEnabled = req.IsScheduleEnabled
            };

            anomaly.Id = await repo.InsertAsync(anomaly);
            await engine.ReloadSchedulesAsync();
            return Results.Created($"/api/anomalies/{anomaly.Id}", anomaly);
        });

        grp.MapPut("/{id:int}", async (int id, CreateAnomalyRequest req, IAnomalyRepository repo, AnomalyEngine engine) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();

            // Enforce Modbus rules for Coil / DiscreteInput
            var direction = req.Direction;
            var pattern = req.Pattern;
            var customMin = req.CustomMin;
            var customMax = req.CustomMax;

            if (req.RegisterType is RegisterType.Coil or RegisterType.DiscreteInput)
            {
                if (direction is not AnomalyDirection.CustomValue)
                    return Results.BadRequest(new { error = "Coil/DiscreteInput anomalies must use CustomValue direction (boolean 0/1 only)" });
                if (pattern is not AnomalyPattern.InstantSpike)
                    return Results.BadRequest(new { error = "Coil/DiscreteInput anomalies only support InstantSpike pattern" });
                customMin = 0;
                customMax = 1;
            }

            existing.Name = req.Name;
            existing.Enabled = req.Enabled;
            existing.SimulatedUnitId = req.SimulatedUnitId;
            existing.RegisterType = req.RegisterType;
            existing.StartAddress = req.StartAddress;
            existing.EndAddress = req.EndAddress;
            existing.Direction = direction;
            existing.Amount = req.Amount;
            existing.CustomPerRegister = req.CustomPerRegister;
            existing.CustomMin = customMin;
            existing.CustomMax = customMax;
            existing.Pattern = pattern;
            existing.RecoveryType = req.RecoveryType;
            existing.DurationSeconds = req.DurationSeconds;
            existing.TriggerMode = req.TriggerMode;
            existing.ScheduleIntervalSeconds = req.ScheduleIntervalSeconds;
            existing.IsScheduleEnabled = req.IsScheduleEnabled;

            await repo.UpdateAsync(existing);
            await engine.ReloadSchedulesAsync();
            return Results.Ok(existing);
        });

        grp.MapDelete("/{id:int}", async (int id, IAnomalyRepository repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();
            await repo.DeleteAsync(id);
            return Results.NoContent();
        });

        grp.MapPost("/{id:int}/trigger", async (int id, IAnomalyRepository repo, AnomalyEngine engine) =>
        {
            var a = await repo.GetByIdAsync(id);
            if (a is null) return Results.NotFound(new { error = "Anomaly not found" });
            if (!a.Enabled) return Results.BadRequest(new { error = "Anomaly is disabled" });
            var success = await engine.TriggerManualAsync(id);
            return success ? Results.Ok(new { triggered = true }) : Results.Conflict(new { error = "Could not trigger anomaly" });
        });

        grp.MapPost("/{id:int}/enable", async (int id, IAnomalyRepository repo, AnomalyEngine engine) =>
        {
            var a = await repo.GetByIdAsync(id);
            if (a is null) return Results.NotFound();
            a.Enabled = true;
            await repo.UpdateAsync(a);
            await engine.ReloadSchedulesAsync();
            return Results.Ok();
        });

        grp.MapPost("/{id:int}/disable", async (int id, IAnomalyRepository repo, AnomalyEngine engine) =>
        {
            var a = await repo.GetByIdAsync(id);
            if (a is null) return Results.NotFound();
            a.Enabled = false;
            await repo.UpdateAsync(a);
            await engine.ReloadSchedulesAsync();
            await engine.StopManualAsync(id); // Stop if active
            return Results.Ok();
        });

        grp.MapPost("/{id:int}/stop", async (int id, AnomalyEngine engine) =>
        {
            var success = await engine.StopManualAsync(id);
            return success ? Results.Ok(new { stopped = true }) : Results.NotFound();
        });
    }
}

public record CreateAnomalyRequest(
    string Name,
    int SimulatedUnitId,
    RegisterType RegisterType,
    ushort StartAddress,
    ushort EndAddress,
    AnomalyDirection Direction = AnomalyDirection.Increase,
    double Amount = 10,
    bool CustomPerRegister = false,
    double CustomMin = 0,
    double CustomMax = 100,
    AnomalyPattern Pattern = AnomalyPattern.InstantSpike,
    RecoveryType RecoveryType = RecoveryType.Immediate,
    int DurationSeconds = 10,
    TriggerMode TriggerMode = TriggerMode.OnDemand,
    double ScheduleIntervalSeconds = 600,
    bool IsScheduleEnabled = false,
    bool Enabled = true
);
