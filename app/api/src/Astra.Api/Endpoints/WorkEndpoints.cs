using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Work;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// P5/P6 — queued multi-routine agent work.
///
///   POST /api/v1/work-runs                       queue a wave or a routine list → 202 {runId}
///   GET  /api/v1/work-runs?corpusId=             recent runs
///   GET  /api/v1/work-runs/{id}                  progress, items, serial-vs-parallel metrics
///   POST /api/v1/work-runs/{id}/{action}         pause | resume | cancel | retry-failed
///   GET  /api/v1/review-queue?corpusId=          specs waiting for an SME
///
/// Queueing and control are the engineer's (admin may queue spec drafting and
/// control runs); nothing here can sign, approve or commit.
/// </summary>
public static class WorkEndpoints
{
    public static IEndpointRouteBuilder MapWorkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/work-runs", async (
            WorkRunBody body, WorkRunService work, AppDbContext db, DevPersonaContext actor, CancellationToken ct) =>
        {
            var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == body.CorpusId, ct);
            if (corpus is null) return Error(new(404, "corpus.not_found", "No such programme."));
            var stages = WorkStages.Normalise(body.Stages);
            if (!WorkRunService.MayRun(actor.Persona, stages))
                return Error(new(403, "persona.forbidden", "Queueing agent work needs the engineer persona (admin may queue spec drafting only)."));

            IReadOnlyList<(Guid, int)> routines;
            Guid? waveId = null;
            string kind, label;
            if (body.RoutineIds is { Length: > 0 })
            {
                routines = body.RoutineIds.Distinct().Select(id => (id, 0)).ToList();
                kind = "routines";
                label = $"{routines.Count} routine(s) · {corpus.Name}";
            }
            else
            {
                var (selection, error) = await work.SelectWaveAsync(corpus.Id, body.Wave, body.AllWaves, stages, ct);
                if (error is not null) return Error(error);
                routines = selection!.Routines;
                waveId = selection.Waves.Count == 1 ? selection.Waves[0].Id : null;
                kind = "wave";
                label = body.AllWaves ? $"All waves · {corpus.Name}" : $"Wave {selection.Waves[0].WaveNumber} · {corpus.Name}";
            }

            var (run, preview, startError) = await work.StartAsync(new WorkRunRequest(
                kind, corpus.Id, routines, stages, label, body.TargetStack, body.BudgetUsd, body.Caps, body.ConversationId, waveId), actor, ct);
            if (startError is not null) return Error(startError);
            return Results.Accepted($"/api/v1/work-runs/{run!.Id}", new
            {
                runId = run.Id,
                label = run.Label,
                routines = preview!.Plan.RoutineCount,
                items = preview.Plan.Items.Count,
                skipped = preview.Plan.Skipped,
                warnings = preview.Warnings,
                statusUrl = $"/api/v1/work-runs/{run.Id}",
            });
        });

        app.MapGet("/api/v1/work-runs", async (Guid? corpusId, int? limit, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.WorkRuns.AsNoTracking().AsQueryable();
            if (corpusId is { } c) q = q.Where(r => r.CorpusId == c);
            var runs = await q.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit ?? 20, 1, 100)).ToListAsync(ct);
            return Results.Ok(new { items = runs });
        });

        app.MapGet("/api/v1/work-runs/{id:guid}", async (Guid id, WorkQueue queue, AppDbContext db, CancellationToken ct) =>
        {
            var snapshot = await queue.SnapshotAsync(id, ct);
            if (snapshot is null) return Error(new(404, "work.run_not_found", "No such run."));
            var items = await db.WorkItems.AsNoTracking().Where(w => w.RunId == id)
                .OrderBy(w => w.Priority).ThenBy(w => w.CreatedAt)
                .Select(w => new
                {
                    w.Id, w.Kind, subroutineId = w.SubjectId, name = w.SubjectName, w.State, w.Attempts, w.DependsOn,
                    w.Error, w.StartedAt, w.CompletedAt, w.CostUsd, w.Calls, w.InputTokens, w.OutputTokens,
                })
                .ToListAsync(ct);
            return Results.Ok(new { run = snapshot, items });
        });

        app.MapPost("/api/v1/work-runs/{id:guid}/{action}", async (
            Guid id, string action, WorkControlBody? body, WorkRunService work, WorkQueue queue, DevPersonaContext actor, CancellationToken ct) =>
        {
            var (message, error) = await work.ControlAsync(id, action, body?.BudgetUsd, actor, ct);
            if (error is not null) return Error(error);
            return Results.Ok(new { message, run = await queue.SnapshotAsync(id, ct) });
        });

        app.MapGet("/api/v1/review-queue", async (Guid? corpusId, int? limit, WorkRunService work, CancellationToken ct) =>
            Results.Ok(new { items = await work.ReviewQueueAsync(corpusId, limit ?? 50, ct) }));

        return app;
    }

    private static IResult Error(WorkError e) =>
        Results.Json(new { error = new { code = e.Code, message = e.Message } }, statusCode: e.Status);
}

public sealed record WorkRunBody(
    Guid CorpusId,
    int? Wave = null,
    bool AllWaves = false,
    Guid[]? RoutineIds = null,
    string[]? Stages = null,
    string? TargetStack = null,
    decimal? BudgetUsd = null,
    Dictionary<string, int>? Caps = null,
    Guid? ConversationId = null);

public sealed record WorkControlBody(decimal? BudgetUsd = null);
