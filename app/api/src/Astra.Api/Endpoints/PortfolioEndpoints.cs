using Astra.Api.Auth;
using Astra.Api.Llm.Dependency;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 8.0.d — Portfolio dashboard read surface.
///
///   GET /api/v1/platform/portfolio-summary  (admin)
///       Cross-corpus aggregates: totals + per-corpus rollup + LLM
///       cost + recent activity. Read-only; no schema additions.
///
/// Admin-only because the data spans every corpus and includes LLM
/// cost totals — engineering teams typically don't want cross-team
/// cost visibility leaking into engineer-persona dashboards.
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/platform/portfolio-summary", async (
            PortfolioMetricsService svc,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);

            var s = await svc.GetSummaryAsync(ct);
            return Results.Ok(new
            {
                totals = new
                {
                    corpusCount = s.Totals.CorpusCount,
                    totalRoutines = s.Totals.TotalRoutines,
                    signedCount = s.Totals.SignedCount,
                    scaffoldedCount = s.Totals.ScaffoldedCount,
                    committedCount = s.Totals.CommittedCount,
                    draftCount = s.Totals.DraftCount,
                    parsedCount = s.Totals.ParsedCount,
                },
                llmTotals = new
                {
                    callCount = s.LlmTotals.CallCount,
                    inputTokens = s.LlmTotals.InputTokens,
                    outputTokens = s.LlmTotals.OutputTokens,
                    costUsd = s.LlmTotals.CostUsd,
                },
                corpora = s.Corpora.Select(c => new
                {
                    corpusId = c.CorpusId,
                    name = c.Name,
                    state = c.State,
                    fileCount = c.FileCount,
                    totalLoc = c.TotalLoc,
                    lastActivity = c.LastActivity,
                    counts = new
                    {
                        total = c.Counts.Total,
                        signed = c.Counts.Signed,
                        scaffolded = c.Counts.Scaffolded,
                        committed = c.Counts.Committed,
                        draft = c.Counts.Draft,
                        parsed = c.Counts.Parsed,
                    },
                    planStatus = c.PlanStatus,
                    planId = c.PlanId,
                    totalWaves = c.TotalWaves,
                }),
                recent = s.Recent.Select(e => new
                {
                    eventType = e.EventType,
                    actorDisplay = e.ActorDisplay,
                    actorPersona = e.ActorPersona,
                    targetType = e.TargetType,
                    targetId = e.TargetId,
                    occurredAt = e.OccurredAt,
                }),
            });
        });

        return app;
    }
}
