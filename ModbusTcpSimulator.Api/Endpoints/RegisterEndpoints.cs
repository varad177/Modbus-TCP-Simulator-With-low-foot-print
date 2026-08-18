using ModbusTcpSimulator.Core.Models;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Api.Services;

namespace ModbusTcpSimulator.Api.Endpoints;

public static class RegisterEndpoints
{
    public static void MapRegisterEndpoints(this WebApplication app)
    {
        // ── Flat list of ALL register configurations (for UI) ────────────
        app.MapGet("/api/register-configurations", async (IRegisterRepository repo) =>
            Results.Ok(await repo.GetAllAsync()));

        app.MapPost("/api/units/{unitId:int}/registers", async (
            int unitId,
            CreateRegisterRequest req,
            IRegisterRepository repo,
            IUnitRepository unitRepo,
            SimulationWorker worker) =>
        {
            var unit = await unitRepo.GetByIdAsync(unitId);
            if (unit is null) return Results.NotFound(new { error = "Unit not found" });

            // Enforce Modbus rules for Coil / DiscreteInput
            var registerType = req.RegisterType;
            var dataType = req.DataType;
            var minValue = req.MinValue;
            var maxValue = req.MaxValue;
            var generationType = req.GenerationType;

            if (registerType is RegisterType.Coil or RegisterType.DiscreteInput)
            {
                dataType = DataType.Bool;
                minValue = 0;
                maxValue = 1;
                // Only Constant and Random make sense for boolean registers
                if (generationType is not (GenerationType.Constant or GenerationType.Random))
                    generationType = GenerationType.Random;
            }

            // Validation
            if (req.StartAddress > req.EndAddress)
                return Results.BadRequest(new { error = "StartAddress must be <= EndAddress" });
            if (minValue > maxValue)
                return Results.BadRequest(new { error = "MinValue must be <= MaxValue" });
            if (req.UpdateIntervalMs < 50)
                return Results.BadRequest(new { error = "UpdateIntervalMs must be >= 50" });

            // Check for overlapping address ranges on the same unit + register type
            var existingRegs = await repo.GetByUnitIdAsync(unitId);
            var overlap = existingRegs.FirstOrDefault(r =>
                r.RegisterType == registerType &&
                r.StartAddress <= req.EndAddress &&
                r.EndAddress >= req.StartAddress);
            if (overlap != null)
                return Results.BadRequest(new { error = $"Address range {req.StartAddress}-{req.EndAddress} overlaps with existing register {overlap.StartAddress}-{overlap.EndAddress} (id={overlap.Id})" });

            // Auto-clamp InitialValue instead of rejecting
            var initialValue = req.InitialValue;
            if (initialValue < minValue) initialValue = minValue;
            if (initialValue > maxValue) initialValue = maxValue;

            var cfg = new RegisterConfiguration
            {
                SimulatedUnitId = unitId,
                RegisterType = registerType,
                StartAddress = req.StartAddress,
                EndAddress = req.EndAddress,
                DataType = dataType,
                ByteOrder = req.ByteOrder,
                Enabled = req.Enabled,
                GenerationType = generationType,
                ConstantValue = req.ConstantValue,
                MinValue = minValue,
                MaxValue = maxValue,
                InitialValue = initialValue,
                IncrementStep = req.IncrementStep,
                SinePeriodSeconds = req.SinePeriodSeconds,
                ScatternessType = req.ScatternessType,
                ScatternessValue = req.ScatternessValue,
                UpdateIntervalMs = req.UpdateIntervalMs
            };

            cfg.Id = await repo.InsertAsync(cfg);
            await worker.ReloadAsync();
            return Results.Created($"/api/register-configurations/{cfg.Id}", cfg);
        });

        app.MapPut("/api/register-configurations/{id:int}", async (
            int id,
            CreateRegisterRequest req,
            IRegisterRepository repo,
            SimulationWorker worker) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();

            // Enforce Modbus rules for Coil / DiscreteInput
            var registerType = req.RegisterType;
            var dataType = req.DataType;
            var minValue = req.MinValue;
            var maxValue = req.MaxValue;
            var generationType = req.GenerationType;

            if (registerType is RegisterType.Coil or RegisterType.DiscreteInput)
            {
                dataType = DataType.Bool;
                minValue = 0;
                maxValue = 1;
                if (generationType is not (GenerationType.Constant or GenerationType.Random))
                    generationType = GenerationType.Random;
            }

            // Auto-clamp InitialValue
            var initialValue = req.InitialValue;
            if (initialValue < minValue) initialValue = minValue;
            if (initialValue > maxValue) initialValue = maxValue;

            // Check for overlapping address ranges on the same unit + register type (excluding self)
            var existingRegs = await repo.GetByUnitIdAsync(existing.SimulatedUnitId);
            var overlap = existingRegs.FirstOrDefault(r =>
                r.Id != id &&
                r.RegisterType == registerType &&
                r.StartAddress <= req.EndAddress &&
                r.EndAddress >= req.StartAddress);
            if (overlap != null)
                return Results.BadRequest(new { error = $"Address range {req.StartAddress}-{req.EndAddress} overlaps with existing register {overlap.StartAddress}-{overlap.EndAddress} (id={overlap.Id})" });

            if (req.SimulatedUnitId != 0) existing.SimulatedUnitId = req.SimulatedUnitId;
            existing.RegisterType = registerType;
            existing.StartAddress = req.StartAddress;
            existing.EndAddress = req.EndAddress;
            existing.DataType = dataType;
            existing.ByteOrder = req.ByteOrder;
            existing.Enabled = req.Enabled;
            existing.GenerationType = generationType;
            existing.ConstantValue = req.ConstantValue;
            existing.MinValue = minValue;
            existing.MaxValue = maxValue;
            existing.InitialValue = initialValue;
            existing.IncrementStep = req.IncrementStep;
            existing.SinePeriodSeconds = req.SinePeriodSeconds;
            existing.ScatternessType = req.ScatternessType;
            existing.ScatternessValue = req.ScatternessValue;
            existing.UpdateIntervalMs = req.UpdateIntervalMs;

            await repo.UpdateAsync(existing);
            await worker.ReloadAsync();
            return Results.Ok(existing);
        });

        app.MapDelete("/api/register-configurations/{id:int}", async (
            int id,
            IRegisterRepository repo,
            SimulationWorker worker) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();
            await repo.DeleteAsync(id);
            await worker.ReloadAsync();
            return Results.NoContent();
        });

        // ── Batch split a range into individual single-address configs ──
        app.MapPost("/api/register-configurations/{id:int}/split", async (
            int id,
            IRegisterRepository repo,
            IUnitRepository unitRepo,
            SimulationWorker worker) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = "Register not found" });
            if (existing.StartAddress == existing.EndAddress)
                return Results.BadRequest(new { error = "Already a single register" });

            var unit = await unitRepo.GetByIdAsync(existing.SimulatedUnitId);
            if (unit is null) return Results.BadRequest(new { error = "Unit not found" });

            // Delete the original range
            await repo.DeleteAsync(id);

            // Create individual configs for each address
            for (ushort addr = existing.StartAddress; addr <= existing.EndAddress; addr++)
            {
                var cfg = new RegisterConfiguration
                {
                    SimulatedUnitId = existing.SimulatedUnitId,
                    RegisterType = existing.RegisterType,
                    StartAddress = addr,
                    EndAddress = addr,
                    DataType = existing.DataType,
                    ByteOrder = existing.ByteOrder,
                    Enabled = existing.Enabled,
                    GenerationType = existing.GenerationType,
                    ConstantValue = existing.ConstantValue,
                    MinValue = existing.MinValue,
                    MaxValue = existing.MaxValue,
                    InitialValue = existing.InitialValue,
                    IncrementStep = existing.IncrementStep,
                    SinePeriodSeconds = existing.SinePeriodSeconds,
                    ScatternessType = existing.ScatternessType,
                    ScatternessValue = existing.ScatternessValue,
                    UpdateIntervalMs = existing.UpdateIntervalMs
                };
                cfg.Id = await repo.InsertAsync(cfg);
            }

            await worker.ReloadAsync();
            return Results.Ok(new { message = "Range split into individual registers" });
        });
    }
}

public record CreateRegisterRequest(
    RegisterType RegisterType,
    ushort StartAddress,
    ushort EndAddress,
    int SimulatedUnitId = 0,
    DataType DataType = DataType.UInt16,
    ByteOrder ByteOrder = ByteOrder.BigEndian,
    bool Enabled = true,
    GenerationType GenerationType = GenerationType.Random,
    double ConstantValue = 0,
    double MinValue = 0,
    double MaxValue = 100,
    double InitialValue = 0,
    double IncrementStep = 1,
    double SinePeriodSeconds = 60,
    ScatternessType ScatternessType = ScatternessType.None,
    double ScatternessValue = 0,
    int UpdateIntervalMs = 1000
);
