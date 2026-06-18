using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class CorpusEndpoints
{
    public static IEndpointRouteBuilder MapCorpusEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/v1/corpora");

        grp.MapGet("", async (AppDbContext db, CancellationToken ct) =>
        {
            // Phase 10.1.b.2 — surface the corpus's dominant source
            // language so the Projects list can colour-code without
            // regex-matching the corpus name. Aggregate is computed
            // over the LATEST SourceVersion only (a re-ingest that
            // dropped legacy Fortran files shouldn't keep the corpus
            // tagged Fortran). Most-common-language wins; null on
            // ties or when the corpus has no parsed subroutines yet.
            var corpora = await db.Corpora
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.SourceType,
                    c.State,
                    c.FileCount,
                    c.TotalLoc,
                    c.UpdatedAt,
                    LatestVersionId = c.Versions
                        .OrderByDescending(v => v.IngestedAt)
                        .Select(v => (Guid?)v.Id)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct);

            var latestVersionIds = corpora
                .Where(c => c.LatestVersionId.HasValue)
                .Select(c => c.LatestVersionId!.Value)
                .ToList();

            var langCounts = latestVersionIds.Count == 0
                ? new List<LangCount>()
                : await db.Subroutines
                    .Where(s => latestVersionIds.Contains(s.SourceFile!.SourceVersionId))
                    .GroupBy(s => new { Vid = s.SourceFile!.SourceVersionId, s.SourceLanguage })
                    .Select(g => new LangCount(g.Key.Vid, g.Key.SourceLanguage, g.Count()))
                    .ToListAsync(ct);

            // Per-version: pick the language with the strict-majority
            // count. Ties resolve to null so the UI can render a
            // neutral pill instead of arbitrarily picking one branch.
            var langByVersion = langCounts
                .GroupBy(x => x.VersionId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var max = g.Max(x => x.Count);
                        var leaders = g.Where(x => x.Count == max).ToList();
                        return leaders.Count == 1 ? leaders[0].SourceLanguage : null;
                    });

            var rows = corpora.Select(c =>
            {
                string? lang = null;
                if (c.LatestVersionId.HasValue
                    && langByVersion.TryGetValue(c.LatestVersionId.Value, out var picked))
                {
                    lang = picked;
                }
                return new CorpusListItem(
                    c.Id,
                    c.Name,
                    c.SourceType,
                    c.State,
                    c.FileCount,
                    c.TotalLoc,
                    lang,
                    c.UpdatedAt);
            }).ToList();

            return Results.Ok(new { data = rows });
        });

        grp.MapGet("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var corpus = await db.Corpora
                .Include(c => c.Versions)
                    .ThenInclude(v => v.Files)
                        .ThenInclude(f => f.Subroutines)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (corpus is null) return Results.NotFound(new { error = new { code = "corpus.not_found" } });

            var latest = corpus.Versions.OrderByDescending(v => v.IngestedAt).FirstOrDefault();

            // Phase C UX polish: surface re-sync lineage on each subroutine.
            // A subroutine "carried forward" iff a Spec row exists with
            // SubroutineId pointing at it AND PreviousSpecId is non-null.
            var subroutineIds = latest?.Files.SelectMany(f => f.Subroutines).Select(s => s.Id).ToList()
                                ?? new List<Guid>();
            var carriedMap = subroutineIds.Count == 0
                ? new Dictionary<Guid, Guid>()
                : await db.Specs.AsNoTracking()
                    .Where(s => subroutineIds.Contains(s.SubroutineId) && s.PreviousSpecId != null)
                    .ToDictionaryAsync(s => s.SubroutineId, s => s.PreviousSpecId!.Value, ct);

            return Results.Ok(new
            {
                id = corpus.Id,
                name = corpus.Name,
                sourceType = corpus.SourceType,
                state = corpus.State,
                fileCount = corpus.FileCount,
                totalLoc = corpus.TotalLoc,
                createdAt = corpus.CreatedAt,
                updatedAt = corpus.UpdatedAt,
                latestVersion = latest is null ? null : new
                {
                    id = latest.Id,
                    ingestedAt = latest.IngestedAt,
                    files = latest.Files.OrderBy(f => f.RelativePath).Select(f => new
                    {
                        id = f.Id,
                        relativePath = f.RelativePath,
                        lineCount = f.LineCount,
                        fileHash = f.FileHash,
                        subroutines = f.Subroutines.OrderBy(s => s.LineStart).Select(s => new
                        {
                            id = s.Id,
                            name = s.Name,
                            lineStart = s.LineStart,
                            lineEnd = s.LineEnd,
                            state = s.State,
                            carriedForward = carriedMap.ContainsKey(s.Id),
                            previousSpecId = carriedMap.TryGetValue(s.Id, out var prev) ? prev : (Guid?)null,
                        }),
                    }),
                },
            });
        });

        return app;
    }

    private sealed record CorpusListItem(
        Guid Id,
        string Name,
        string SourceType,
        string State,
        int FileCount,
        int TotalLoc,
        string? SourceLanguage,
        DateTimeOffset UpdatedAt);

    private sealed record LangCount(Guid VersionId, string SourceLanguage, int Count);
}
