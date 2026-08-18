using ModbusTcpSimulator.Core.Models;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Api.Services;
using ModbusTcpSimulator.Core.State;

namespace ModbusTcpSimulator.Api.Endpoints;

public static class UnitEndpoints
{
    public static void MapUnitEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/units");

        grp.MapGet("/", async (IUnitRepository repo) =>
            Results.Ok(await repo.GetAllAsync()));

        grp.MapGet("/{id:int}", async (int id, IUnitRepository repo) =>
        {
            var unit = await repo.GetByIdAsync(id);
            return unit is null ? Results.NotFound() : Results.Ok(unit);
        });

        grp.MapPost("/", async (CreateUnitRequest req, IUnitRepository repo, SimulatorState state) =>
        {
            if (req.UnitId is < 1 or > 247)
                return Results.BadRequest(new { error = "Unit ID must be between 1 and 247" });

            if (await repo.GetByUnitIdAsync(req.UnitId) is not null)
                return Results.Conflict(new { error = $"Unit ID {req.UnitId} already exists" });

            var unit = new SimulatedUnit
            {
                UnitId = req.UnitId,
                Label = req.Label,
                Enabled = req.Enabled
            };
            unit.Id = await repo.InsertAsync(unit);
            return Results.Created($"/api/units/{unit.Id}", unit);
        });

        grp.MapPut("/{id:int}", async (int id, UpdateUnitRequest req, IUnitRepository repo) =>
        {
            var unit = await repo.GetByIdAsync(id);
            if (unit is null) return Results.NotFound();

            unit.Label = req.Label;
            unit.Enabled = req.Enabled;
            await repo.UpdateAsync(unit);
            return Results.Ok(unit);
        });

        grp.MapDelete("/{id:int}", async (int id, IUnitRepository repo, SimulatorState state, IRegisterRepository regRepo) =>
        {
            var unit = await repo.GetByIdAsync(id);
            if (unit is null) return Results.NotFound();
            state.RemoveUnit(unit.UnitId);
            await repo.DeleteAsync(id);
            return Results.NoContent();
        });

        grp.MapGet("/{id:int}/registers", async (int id, IRegisterRepository regRepo, IUnitRepository unitRepo) =>
        {
            var unit = await unitRepo.GetByIdAsync(id);
            if (unit is null) return Results.NotFound();
            return Results.Ok(await regRepo.GetByUnitIdAsync(id));
        });
    }
}

public record CreateUnitRequest(byte UnitId, string? Label, bool Enabled = true);
public record UpdateUnitRequest(string? Label, bool Enabled);
