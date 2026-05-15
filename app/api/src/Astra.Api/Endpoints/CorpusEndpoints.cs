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
            var rows = await db.Corpora
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new CorpusListItem(
                    c.Id,
                    c.Name,
                    c.SourceType,
                    c.State,
                    c.FileCount,
                    c.TotalLoc,
                    c.UpdatedAt))
                .ToListAsync(ct);
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
        DateTimeOffset UpdatedAt);
}
