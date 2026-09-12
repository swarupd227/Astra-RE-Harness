using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm.PatternAnalysis;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 12.0 — Pattern-analysis API surface.
///
///   POST /api/v1/corpora/{id}/pattern-analysis            admin-only, starts the pass (202 + runId)
///   GET  /api/v1/pattern-analysis/runs/{runId}             poll run status
///   GET  /api/v1/corpora/{id}/pattern-clusters              list clusters from the corpus's latest run
///
/// Read paths are open to any authenticated persona. The trigger is
/// admin-gated because it's a corpus-wide bulk operation with real LLM
/// cost (bounded-concurrency extraction over every un-extracted routine,
/// plus one clustering call), same posture as Harmonisation.
/// </summary>
public static class PatternAnalysisEndpoints
{
    public static IEndpointRouteBuilder MapPatternAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        // ?stages=survey,cluster (default) | extract,cluster (the old full-
        // extraction discovery path) | survey (digests only, for Assessment).
        app.MapPost("/api/v1/corpora/{id:guid}/pattern-analysis", async (
            Guid id,
            bool? force,
            string? stages,
            PatternAnalysisOrchestrator orchestrator,
            AppDbContext db,
            DevPersonaContext actor,
            Astra.Api.Copilot.Narrator narrator,
            Astra.Api.Conversations.ConversationService conversations,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            // WS2 — whichever way a run starts (this button or the copilot),
            // the Discovery agent narrates it into the programme's thread.
            async Task TrackAsync(Guid runId)
            {
                try
                {
                    var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
                    var thread = await conversations.EnsureProgrammeAsync(id, ct);
                    narrator.Track(runId, thread.Id, "discovery", "pattern-analysis", corpus?.Name ?? "programme", id,
                        actor.Persona.ToString().ToLowerInvariant(), actor.DisplayName);
                }
                catch (Exception) { /* narration is best-effort */ }
            }

            // One run per corpus at a time. Without this, a second click
            // starts a rival pass over the same routines; the two collide on
            // each other's EXTRACTING rows and book the losses as failures.
            var inFlight = await db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == id && (r.State == "QUEUED" || r.State == "RUNNING"))
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (inFlight is not null)
            {
                await TrackAsync(inFlight.Id);
                return Results.Accepted($"/api/v1/pattern-analysis/runs/{inFlight.Id}", new
                {
                    runId = inFlight.Id,
                    statusUrl = $"/api/v1/pattern-analysis/runs/{inFlight.Id}",
                    alreadyRunning = true,
                    startedAt = inFlight.StartedAt,
                    summary = inFlight.Summary,
                });
            }

            // A paused / interrupted run resumes instead of starting over —
            // unless the caller explicitly forces a fresh pass.
            if (!(force ?? false))
            {
                var resumable = await db.PatternAnalysisRuns.AsNoTracking()
                    .Where(r => r.CorpusId == id && r.State == "RESUMABLE")
                    .OrderByDescending(r => r.StartedAt)
                    .FirstOrDefaultAsync(ct);
                if (resumable is not null && await orchestrator.ResumeAsync(resumable.Id))
                {
                    await TrackAsync(resumable.Id);
                    return Results.Accepted($"/api/v1/pattern-analysis/runs/{resumable.Id}", new
                    {
                        runId = resumable.Id,
                        statusUrl = $"/api/v1/pattern-analysis/runs/{resumable.Id}",
                        resumed = true,
                    });
                }
            }

            try
            {
                var runId = await orchestrator.StartAsync(id, force ?? false, actor.DisplayName, stages, ct);
                await TrackAsync(runId);
                return Results.Accepted($"/api/v1/pattern-analysis/runs/{runId}", new
                {
                    runId,
                    statusUrl = $"/api/v1/pattern-analysis/runs/{runId}",
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "pattern_analysis.precondition_failed", message = ex.Message },
                });
            }
        });

        // Phase 15.2 — run control. Cancel is terminal; pause parks the run
        // as RESUMABLE with every digest kept; resume continues it.
        app.MapPost("/api/v1/pattern-analysis/runs/{runId:guid}/cancel", async (
            Guid runId, PatternAnalysisOrchestrator orchestrator, DevPersonaContext actor) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            return await orchestrator.StopAsync(runId, resumable: false)
                ? Results.Accepted($"/api/v1/pattern-analysis/runs/{runId}", new { runId, state = "CANCELLING" })
                : Results.Conflict(new { error = new { code = "pattern_analysis_run.not_active" } });
        });

        app.MapPost("/api/v1/pattern-analysis/runs/{runId:guid}/pause", async (
            Guid runId, PatternAnalysisOrchestrator orchestrator, DevPersonaContext actor) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            return await orchestrator.StopAsync(runId, resumable: true)
                ? Results.Accepted($"/api/v1/pattern-analysis/runs/{runId}", new { runId, state = "PAUSING" })
                : Results.Conflict(new { error = new { code = "pattern_analysis_run.not_active" } });
        });

        app.MapPost("/api/v1/pattern-analysis/runs/{runId:guid}/resume", async (
            Guid runId, PatternAnalysisOrchestrator orchestrator, DevPersonaContext actor) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            return await orchestrator.ResumeAsync(runId)
                ? Results.Accepted($"/api/v1/pattern-analysis/runs/{runId}", new { runId, state = "RUNNING", resumed = true })
                : Results.Conflict(new { error = new { code = "pattern_analysis_run.not_resumable" } });
        });

        // SSE stream for an active run, off the structured RunEventBus.
        //   - `log` events stay unnamed with {message} so the existing
        //     EventSource.onmessage consumer keeps working;
        //   - `progress` / `stage` / `state` / `item` arrive as named events
        //     for the progress bar and activity feed;
        //   - every event carries `id: <seq>` and the route honours
        //     Last-Event-ID / ?afterSeq= so a reconnecting browser replays
        //     what it missed instead of starting blind.
        app.MapGet("/api/v1/pattern-analysis/runs/{runId:guid}/logs", async (
            Guid runId,
            long? afterSeq,
            Astra.Api.Runs.RunEventBus bus,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.Body.FlushAsync(ct);

            var since = afterSeq ?? 0;
            if (ctx.Request.Headers.TryGetValue("Last-Event-ID", out var lastId)
                && long.TryParse(lastId.ToString(), out var parsed))
            {
                since = Math.Max(since, parsed);
            }

            try
            {
                await foreach (var evt in bus.SubscribeAsync(runId, since, ct))
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        message = evt.Message,
                        agent = evt.Agent,
                        stage = evt.Stage,
                        type = evt.Type,
                        seq = evt.Seq,
                        ts = evt.Ts,
                        data = evt.Data,
                    });
                    var frame = evt.Type == "log"
                        ? $"id: {evt.Seq}\ndata: {payload}\n\n"
                        : $"id: {evt.Seq}\nevent: {evt.Type}\ndata: {payload}\n\n";
                    await ctx.Response.WriteAsync(frame, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                if (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The browser closed the stream — not an error, so no 500 in the log.
            }
        });

        app.MapGet("/api/v1/pattern-analysis/runs/{runId:guid}", async (
            Guid runId, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.PatternAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null)
                return Results.NotFound(new { error = new { code = "pattern_analysis_run.not_found" } });
            return Results.Ok(RenderRun(run));
        });

        app.MapGet("/api/v1/corpora/{id:guid}/pattern-analysis-runs", async (
            Guid id, AppDbContext db, int? limit, CancellationToken ct) =>
        {
            var rows = await db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == id)
                .OrderByDescending(r => r.StartedAt)
                .Take(Math.Clamp(limit ?? 20, 1, 100))
                .ToListAsync(ct);
            return Results.Ok(new { data = rows.Select(RenderRun) });
        });

        app.MapGet("/api/v1/corpora/{id:guid}/pattern-clusters", async (
            Guid id, AppDbContext db, CancellationToken ct) =>
        {
            // Clusters belong to the corpus's MOST RECENT completed run —
            // clustering is a discovery snapshot, not additive history
            // (the orchestrator deletes a corpus's prior clusters before
            // writing a new run's).
            var latestRun = await db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == id && (r.State == "SUCCEEDED" || r.State == "PARTIAL"))
                .OrderByDescending(r => r.CompletedAt)
                .FirstOrDefaultAsync(ct);
            if (latestRun is null)
                return Results.Ok(new { run = (object?)null, clusters = Array.Empty<object>() });

            var clusters = await db.PatternClusters.AsNoTracking()
                .Where(c => c.PatternAnalysisRunId == latestRun.Id)
                .OrderByDescending(c => c.MemberCount)
                .ThenBy(c => c.Label)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                run = RenderRun(latestRun),
                clusters = clusters.Select(RenderCluster),
            });
        });

        return app;
    }

    // RenderRun/RenderCluster are internal so ProjectExportService can bundle
    // pattern-analysis JSON with the exact shape the live endpoint serves.
    internal static object RenderRun(PatternAnalysisRun r) => new
    {
        id = r.Id,
        corpusId = r.CorpusId,
        sourceVersionId = r.SourceVersionId,
        stagesRequested = r.StagesRequested,
        state = r.State,
        heartbeatAt = r.HeartbeatAt,
        cancelRequested = r.CancelRequested,
        metrics = r.MetricsJson is null
            ? null
            : (JsonElement?)JsonDocument.Parse(r.MetricsJson).RootElement,
        summary = r.Summary,
        errorSummary = r.ErrorSummary,
        triggeredBy = r.TriggeredBy,
        startedAt = r.StartedAt,
        completedAt = r.CompletedAt,
    };

    internal static object RenderCluster(PatternCluster c) => new
    {
        id = c.Id,
        patternAnalysisRunId = c.PatternAnalysisRunId,
        corpusId = c.CorpusId,
        claimKindSignature = c.ClaimKindSignature,
        label = c.Label,
        suggestedArchetypeName = c.SuggestedArchetypeName,
        rationale = c.Rationale,
        members = ParseMembers(c.MemberSubroutineIdsJson),
        memberCount = c.MemberCount,
        createdAt = c.CreatedAt,
    };

    private static object ParseMembers(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()) ?? Array.Empty<object>();
        }
        catch { return Array.Empty<object>(); }
    }

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}
