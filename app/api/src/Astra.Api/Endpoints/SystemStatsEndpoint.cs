using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase C / UX polish: a single rollup endpoint the home page uses to
/// answer "what's actually going on here?" without firing five list queries
/// just to compute counts.
///
///   • corpora       : total + state breakdown (PARSED, INGESTING, FAILED, …)
///   • subroutines   : total across all corpora's CURRENT versions only
///                     + state breakdown so the home can surface
///                     "1 subroutine PARSED → ready to extract" type CTAs.
///   • specs         : total + state breakdown (DRAFT/IN_REVIEW/SIGNED/SUPERSEDED)
///   • scaffolds     : count + total TODOs across all generated artifacts
///   • llm           : totalCalls / totalCostUsd / avgLatencyMs / lastCalledAt
///                     for the "Provider" card and cost transparency.
/// </summary>
public static class SystemStatsEndpoint
{
    public static IEndpointRouteBuilder MapSystemStatsEndpoint(this IEndpointRouteBuilder app)
    {
        // GET /api/v1/system/problems — what needs someone, for System health: validation runs still "running" long
        // after any run finishes (a restarted container leaves them stuck; there is no reconciliation yet) and the
        // validation runs that failed in the last day, each with the routine they belong to.
        app.MapGet("/api/v1/system/problems", async (AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var stuckBefore = now.AddMinutes(-30);
            var since = now.AddDays(-1);
            var rows = await db.ValidationRuns.AsNoTracking()
                .Where(v => (v.Status == "RUNNING" && v.StartedAt < stuckBefore)
                         || ((v.Status == "FAILED" || v.Status == "ERRORED") && v.StartedAt >= since))
                .OrderByDescending(v => v.StartedAt)
                // Enough for the page to group a busy day's failures by project (a repair loop can fail dozens of
                // runs in one project) without one project crowding out the rest.
                .Take(300)
                .Select(v => new
                {
                    id = v.Id,
                    scaffoldId = v.ScaffoldId,
                    stage = v.Stage,
                    status = v.Status,
                    summary = v.Summary,
                    startedAt = v.StartedAt,
                    routineName = v.Spec!.Subroutine!.Name,
                    subroutineId = v.Spec!.SubroutineId,
                    corpusId = (Guid?)v.Spec!.Subroutine!.SourceFile!.SourceVersion!.CorpusId,
                    corpusName = v.Spec!.Subroutine!.SourceFile!.SourceVersion!.Corpus!.Name,
                })
                .ToListAsync(ct);
            return Results.Ok(new
            {
                stuck = rows.Where(r => r.status == "RUNNING"),
                failed = rows.Where(r => r.status != "RUNNING"),
            });
        });

        app.MapGet("/api/v1/system/stats", async (AppDbContext db, CancellationToken ct) =>
        {
            // ── Corpora ──────────────────────────────────────────────
            // QA / scratch projects (ProjectSettings.IsTestProject) are left out, as on Home and the project list, so
            // every page counts the same estate. The rule reads the name and settings, so it runs in memory over
            // the (few dozen) project rows.
            var allCorpora = await db.Corpora.AsNoTracking()
                .Select(c => new { c.Id, c.Name, c.SettingsJson, c.State, c.TotalLoc, c.FileCount })
                .ToListAsync(ct);
            var real = allCorpora.Where(c => !Artefacts.ProjectSettings.IsTestProject(c.Name, c.SettingsJson)).ToList();
            var realIds = real.Select(c => c.Id).ToList();
            var corporaTotal = real.Count;
            var corporaByState = real.GroupBy(c => c.State)
                .Select(g => new { state = g.Key, count = g.Count() })
                .ToList();
            var totalLoc = real.Sum(c => (long)c.TotalLoc);
            var totalFiles = real.Sum(c => (long)c.FileCount);

            // ── Subroutines (latest-version-only) ────────────────────
            // Mirrors the gating in /api/v1/subroutines so the home count
            // matches what the search page surfaces.
            var subQuery =
                from s in db.Subroutines.AsNoTracking()
                join f in db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
                join v in db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
                join c in db.Corpora.AsNoTracking() on v.CorpusId equals c.Id
                where c.LatestVersionId == v.Id && realIds.Contains(c.Id)
                select s;
            var subsTotal = await subQuery.CountAsync(ct);
            var subsByState = await subQuery
                .GroupBy(s => s.State)
                .Select(g => new { state = g.Key, count = g.Count() })
                .ToListAsync(ct);

            // ── Specs ────────────────────────────────────────────────
            var specsTotal = await db.Specs.CountAsync(ct);
            var specsByState = await db.Specs.AsNoTracking()
                .GroupBy(s => s.State)
                .Select(g => new { state = g.Key, count = g.Count() })
                .ToListAsync(ct);

            // ── Scaffolds ────────────────────────────────────────────
            var scaffoldsTotal = await db.Scaffolds.CountAsync(ct);
            var scaffoldTodoTotal = await db.Scaffolds.SumAsync(s => (int?)s.TodoCount, ct) ?? 0;

            // ── LLM cost / latency rollup ────────────────────────────
            var llmCount = await db.LlmCalls.CountAsync(ct);
            var llmCostUsd = await db.LlmCalls.SumAsync(c => (decimal?)c.CostUsd, ct) ?? 0m;
            var llmTokensIn = await db.LlmCalls.SumAsync(c => (long?)c.InputTokens, ct) ?? 0L;
            var llmTokensOut = await db.LlmCalls.SumAsync(c => (long?)c.OutputTokens, ct) ?? 0L;
            var llmAvgLatency = await db.LlmCalls.AverageAsync(c => (double?)c.LatencyMs, ct);
            var llmLastCalledAt = await db.LlmCalls.AsNoTracking()
                .OrderByDescending(c => c.CalledAt)
                .Select(c => (DateTimeOffset?)c.CalledAt)
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                corpora = new
                {
                    total = corporaTotal,
                    files = totalFiles,
                    totalLoc,
                    byState = corporaByState.ToDictionary(x => x.state, x => x.count),
                },
                subroutines = new
                {
                    total = subsTotal,
                    byState = subsByState.ToDictionary(x => x.state, x => x.count),
                },
                specs = new
                {
                    total = specsTotal,
                    byState = specsByState.ToDictionary(x => x.state, x => x.count),
                },
                scaffolds = new
                {
                    total = scaffoldsTotal,
                    todoTotal = scaffoldTodoTotal,
                },
                llm = new
                {
                    totalCalls = llmCount,
                    totalCostUsd = llmCostUsd,
                    totalInputTokens = llmTokensIn,
                    totalOutputTokens = llmTokensOut,
                    avgLatencyMs = llmAvgLatency,
                    lastCalledAt = llmLastCalledAt,
                },
            });
        });

        return app;
    }
}
