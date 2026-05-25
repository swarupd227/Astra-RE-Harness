using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm.Dependency;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 8.0.b — Migration-plan read + write surface.
///
///   POST /api/v1/corpora/{id}/migration-plan/generate   (admin) → draft plan
///   POST /api/v1/migration-plans/{id}/approve           (admin) → mark approved
///   POST /api/v1/migration-plans/{id}/archive           (admin) → mark archived
///   GET  /api/v1/corpora/{id}/migration-plan                   → current plan + waves + live counts
///   GET  /api/v1/migration-plans/{id}                          → plan + waves + live counts
///
/// "Current plan" = the approved plan if one exists, otherwise the
/// most recently created draft. Live counts (signed / scaffolded /
/// validated / committed per wave) are computed at GET time — drift
/// happens fast on a working corpus, denormalising would be wrong.
/// </summary>
public static class MigrationPlanEndpoints
{
    public static IEndpointRouteBuilder MapMigrationPlanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/corpora/{id:guid}/migration-plan/generate", async (
            Guid id,
            MigrationPlanRequest? body,
            MigrationPlanner planner,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var strategy = body?.Strategy ?? MigrationPlanner.DefaultStrategy;
            var optionsJson = body?.Options is null
                ? "{}"
                : JsonSerializer.Serialize(body.Options);
            try
            {
                var plan = await planner.GenerateDraftAsync(id, strategy, actor, ct, optionsJson);
                return Results.Ok(RenderPlanSummary(plan));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { code = "migration_plan.unknown_strategy", message = ex.Message } });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = new { code = "migration_plan.precondition_failed", message = ex.Message } });
            }
        });

        // Phase 8.0.e — Strategy listing. Open to every authenticated
        // persona; the descriptions are UI affordance, not a security
        // surface. The picker in the migration-plan page calls this
        // to render the strategy dropdown.
        app.MapGet("/api/v1/migration-plan-strategies", (
            MigrationPlanner planner) =>
        {
            var strategies = planner.AvailableStrategies
                .Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    isDefault = s.Name == MigrationPlanner.DefaultStrategy,
                    optionsSchema = s.OptionsSchema,
                })
                .OrderBy(s => s.isDefault ? 0 : 1)
                .ThenBy(s => s.name, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(new { data = strategies });
        });

        app.MapPost("/api/v1/migration-plans/{id:guid}/approve", async (
            Guid id,
            MigrationPlanner planner,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            try
            {
                var plan = await planner.ApproveAsync(id, actor, ct);
                return Results.Ok(RenderPlanSummary(plan));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = new { code = "migration_plan.cannot_approve", message = ex.Message } });
            }
        });

        app.MapPost("/api/v1/migration-plans/{id:guid}/archive", async (
            Guid id,
            MigrationPlanner planner,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            try
            {
                var plan = await planner.ArchiveAsync(id, actor, ct);
                return Results.Ok(RenderPlanSummary(plan));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = new { code = "migration_plan.cannot_archive", message = ex.Message } });
            }
        });

        app.MapGet("/api/v1/corpora/{id:guid}/migration-plan", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            // Current plan = approved if exists, else most-recent draft.
            var plan = await db.MigrationPlans.AsNoTracking()
                .Where(p => p.CorpusId == id && p.Status != "archived")
                .OrderByDescending(p => p.Status == "approved")
                .ThenByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (plan is null)
                return Results.NotFound(new { error = new { code = "migration_plan.not_found" } });
            return Results.Ok(await BuildDetailAsync(db, plan, ct));
        });

        app.MapGet("/api/v1/migration-plans/{id:guid}", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var plan = await db.MigrationPlans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null)
                return Results.NotFound(new { error = new { code = "migration_plan.not_found" } });
            return Results.Ok(await BuildDetailAsync(db, plan, ct));
        });

        return app;
    }

    private static async Task<object> BuildDetailAsync(AppDbContext db, MigrationPlan plan, CancellationToken ct)
    {
        var waves = await db.MigrationWaves.AsNoTracking()
            .Where(w => w.MigrationPlanId == plan.Id)
            .OrderBy(w => w.WaveNumber)
            .ToListAsync(ct);

        // Pull every routine referenced by any wave so we can render
        // their current state without N+1 queries.
        var allRoutineIds = waves
            .SelectMany(w => ParseGuids(w.PlannedRoutineIdsJson))
            .ToHashSet();
        var subs = allRoutineIds.Count == 0
            ? new List<Subroutine>()
            : await db.Subroutines.AsNoTracking()
                .Where(s => allRoutineIds.Contains(s.Id))
                .ToListAsync(ct);
        var specs = allRoutineIds.Count == 0
            ? new List<Spec>()
            : await db.Specs.AsNoTracking()
                .Where(sp => allRoutineIds.Contains(sp.SubroutineId))
                .OrderByDescending(sp => sp.UpdatedAt)
                .ToListAsync(ct);
        var latestSpecBySub = specs
            .GroupBy(sp => sp.SubroutineId)
            .ToDictionary(g => g.Key, g => g.First());
        var specIds = specs.Select(sp => sp.Id).ToList();
        var scaffolds = specIds.Count == 0
            ? new List<Scaffold>()
            : await db.Scaffolds.AsNoTracking()
                .Where(sc => specIds.Contains(sc.SpecId))
                .OrderByDescending(sc => sc.GeneratedAt)
                .ToListAsync(ct);
        var latestScaffoldBySpec = scaffolds
            .GroupBy(sc => sc.SpecId)
            .ToDictionary(g => g.Key, g => g.First());
        var subById = subs.ToDictionary(s => s.Id);

        var waveDetails = waves.Select(w =>
        {
            var ids = ParseGuids(w.PlannedRoutineIdsJson);
            int signed = 0, scaffolded = 0, committed = 0;
            var routines = ids
                .Select(rid =>
                {
                    if (!subById.TryGetValue(rid, out var sub))
                    {
                        return new
                        {
                            id = rid,
                            name = "(missing)",
                            state = "MISSING",
                            specId = (Guid?)null,
                        };
                    }
                    Spec? spec = latestSpecBySub.GetValueOrDefault(rid);
                    Scaffold? scaffold = spec is null
                        ? null
                        : latestScaffoldBySpec.GetValueOrDefault(spec.Id);
                    var state = ClassifyState(sub, spec, scaffold);
                    if (state == "SIGNED") signed++;
                    else if (state == "SCAFFOLDED") scaffolded++;
                    else if (state == "COMMITTED") { scaffolded++; committed++; signed++; }
                    return new
                    {
                        id = rid,
                        name = sub.Name,
                        state,
                        specId = (Guid?)spec?.Id,
                    };
                })
                .OrderBy(r => r.name, StringComparer.Ordinal)
                .ToList();
            return new
            {
                id = w.Id,
                waveNumber = w.WaveNumber,
                name = w.Name,
                status = w.Status,
                routineCount = w.RoutineCount,
                liveCounts = new { signed, scaffolded, committed },
                routines,
            };
        }).ToList();

        return new
        {
            plan = RenderPlanSummary(plan),
            waves = waveDetails,
        };
    }

    private static string ClassifyState(Subroutine sub, Spec? spec, Scaffold? scaffold)
    {
        if (scaffold is not null && scaffold.State == "COMMITTED") return "COMMITTED";
        if (scaffold is not null) return "SCAFFOLDED";
        if (spec is not null && spec.State == "SIGNED") return "SIGNED";
        if (spec is not null) return spec.State;
        return sub.State;
    }

    private static IReadOnlyList<Guid> ParseGuids(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Guid>();
            var list = new List<Guid>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                    list.Add(g);
            }
            return list;
        }
        catch { return Array.Empty<Guid>(); }
    }

    private static object RenderPlanSummary(MigrationPlan p) => new
    {
        id = p.Id,
        corpusId = p.CorpusId,
        sourceVersionId = p.SourceVersionId,
        status = p.Status,
        strategyName = p.StrategyName,
        totalRoutines = p.TotalRoutines,
        totalWaves = p.TotalWaves,
        summary = p.Summary,
        generatedBy = p.GeneratedBy,
        approvedBy = p.ApprovedBy,
        createdAt = p.CreatedAt,
        approvedAt = p.ApprovedAt,
        archivedAt = p.ArchivedAt,
    };

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}

public sealed record MigrationPlanRequest(string? Strategy, object? Options);
