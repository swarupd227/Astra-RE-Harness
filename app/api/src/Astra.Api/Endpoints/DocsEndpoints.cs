using Astra.Api.Docs;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 11.0 documentation endpoints. This commit ships only the vertical-slice
/// trigger (gated by Dev:ResetEnabled). Phase 11.0.a opens up the production
/// /api/v1/corpora/{id}/docs/generate flow.
/// </summary>
public static class DocsEndpoints
{
    public static IEndpointRouteBuilder MapDocsEndpoints(this IEndpointRouteBuilder app)
    {
        // Vertical-slice trigger. POST /api/v1/dev/docs/slice?corpusId=...&take=10
        // Runs N routine-summary extractions through the real DocSection
        // entity model, returns a JSON summary with section IDs + summaries.
        // Dev-only; production doc generation lives behind 11.0.a's endpoint.
        app.MapPost("/api/v1/dev/docs/slice", async (
            Guid corpusId,
            int? take,
            DocsExtractionService svc,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (!cfg.GetValue("Dev:ResetEnabled", false))
                return Results.NotFound(new { error = new { code = "dev.docs.disabled" } });

            var n = Math.Clamp(take ?? 10, 1, 50);
            var result = await svc.RunSliceAsync(corpusId, n, ct);
            return Results.Ok(new
            {
                generationRunId = result.GenerationRunId,
                requested = result.Requested,
                succeeded = result.Succeeded,
                failed = result.Failed,
                sections = result.Sections,
            });
        });

        // Phase 11.0.a — production routine-summary generator. Returns 202
        // with a generationRunId; the actual work runs in the background
        // and the caller polls /status until State leaves RUNNING.
        // Optional ?take=N caps work for spot-runs; ?force=true wipes any
        // existing routine-summary sections for the same SourceVersion
        // before re-generating.
        app.MapPost("/api/v1/corpora/{corpusId:guid}/docs/generate", async (
            Guid corpusId,
            int? take,
            bool? force,
            Astra.Api.Docs.RoutineSummaryPipeline pipeline,
            CancellationToken ct) =>
        {
            var runId = await pipeline.StartAsync(
                corpusId,
                new Astra.Api.Docs.RoutineSummaryPipeline.GenerateOptions(take, force ?? false),
                ct);
            return Results.Accepted($"/api/v1/docs/runs/{runId}", new
            {
                generationRunId = runId,
                statusUrl = $"/api/v1/docs/runs/{runId}",
            });
        });

        // Status poll for a generation run. Returns state, summary, metrics.
        app.MapGet("/api/v1/docs/runs/{runId:guid}", async (
            Guid runId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var run = await db.DocGenerationRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null)
                return Results.NotFound(new { error = new { code = "docs.run.not_found" } });
            return Results.Ok(new
            {
                id = run.Id,
                corpusId = run.CorpusId,
                state = run.State,
                summary = run.Summary,
                metrics = run.MetricsJson is null
                    ? null
                    : (System.Text.Json.JsonElement?)System.Text.Json.JsonDocument.Parse(run.MetricsJson).RootElement,
                startedAt = run.StartedAt,
                completedAt = run.CompletedAt,
                errorSummary = run.ErrorSummary,
            });
        });

        // Read-back: GET /api/v1/dev/docs/sections?corpusId=...
        // Returns the rendered markdown for every DocSection on the corpus
        // so the slice output can be read by eye.
        app.MapGet("/api/v1/dev/docs/sections", async (
            Guid corpusId,
            AppDbContext db,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (!cfg.GetValue("Dev:ResetEnabled", false))
                return Results.NotFound(new { error = new { code = "dev.docs.disabled" } });

            var sections = await db.DocSections
                .AsNoTracking()
                .Where(s => s.CorpusId == corpusId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    id = s.Id,
                    kind = s.SectionKind,
                    state = s.State,
                    subroutineId = s.SubroutineId,
                    rendered = s.RenderedMarkdown,
                    createdAt = s.CreatedAt,
                })
                .ToListAsync(ct);

            return Results.Ok(new { count = sections.Count, sections });
        });

        return app;
    }
}
