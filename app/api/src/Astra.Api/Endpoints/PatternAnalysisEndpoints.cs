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
        app.MapPost("/api/v1/corpora/{id:guid}/pattern-analysis", async (
            Guid id,
            bool? force,
            PatternAnalysisOrchestrator orchestrator,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            try
            {
                var runId = await orchestrator.StartAsync(id, force ?? false, actor.DisplayName, ct);
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
