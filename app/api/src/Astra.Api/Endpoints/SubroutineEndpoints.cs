using Astra.Api.Persistence;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class SubroutineEndpoints
{
    public static IEndpointRouteBuilder MapSubroutineEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/v1/subroutines");

        // ── Cross-corpus search (Phase C.12) ────────────────────────────
        // Fuzzy name match across every corpus's CURRENT version. Older
        // versions are excluded so the user only sees live routines.
        grp.MapGet("", async (
            string? q,
            Guid? corpus,
            string? state,
            int? limit,
            int? offset,
            AppDbContext db,
            CancellationToken ct) =>
        {
            const int maxLimit = 200;
            var take = Math.Clamp(limit ?? 50, 1, maxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var qTrim = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            var qUpper = qTrim?.ToUpperInvariant();

            // Base query joins subroutine → file → version → corpus, gated
            // so only the corpus's *latest* version shows up.
            var baseQuery =
                from s in db.Subroutines.AsNoTracking()
                join f in db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
                join v in db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
                join c in db.Corpora.AsNoTracking() on v.CorpusId equals c.Id
                where c.LatestVersionId == v.Id
                select new { s, f, c };

            if (corpus.HasValue)
                baseQuery = baseQuery.Where(x => x.c.Id == corpus.Value);

            if (!string.IsNullOrWhiteSpace(state))
            {
                var stateUpper = state.ToUpperInvariant();
                baseQuery = baseQuery.Where(x => x.s.State.ToUpper() == stateUpper);
            }

            if (qUpper is not null)
            {
                // Postgres ILIKE: case-insensitive substring. Fast enough
                // for Phase C corpora; pg_trgm similarity ranking lands in
                // Phase D when the corpus list grows past ~1000 routines.
                baseQuery = baseQuery.Where(x =>
                    EF.Functions.ILike(x.s.Name, $"%{qTrim}%") ||
                    EF.Functions.ILike(x.s.Signature, $"%{qTrim}%"));
            }

            var total = await baseQuery.CountAsync(ct);

            var rows = await baseQuery
                .OrderBy(x => x.c.Name)
                .ThenBy(x => x.f.RelativePath)
                .ThenBy(x => x.s.LineStart)
                .Skip(skip)
                .Take(take)
                .Select(x => new
                {
                    id = x.s.Id,
                    name = x.s.Name,
                    signature = x.s.Signature,
                    lineStart = x.s.LineStart,
                    lineEnd = x.s.LineEnd,
                    state = x.s.State,
                    file = new
                    {
                        id = x.f.Id,
                        relativePath = x.f.RelativePath,
                        lineCount = x.f.LineCount,
                    },
                    corpus = new
                    {
                        id = x.c.Id,
                        name = x.c.Name,
                    },
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                data = rows,
                total,
                limit = take,
                offset = skip,
                hasMore = skip + rows.Count < total,
                query = qTrim,
            });
        });

        grp.MapGet("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var sub = await db.Subroutines
                .Include(s => s.SourceFile)
                    .ThenInclude(f => f!.SourceVersion)
                        .ThenInclude(v => v!.Corpus)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (sub is null) return Results.NotFound(new { error = new { code = "subroutine.not_found" } });

            var file = sub.SourceFile!;
            var version = file.SourceVersion!;
            var corpus = version.Corpus!;

            return Results.Ok(new
            {
                id = sub.Id,
                name = sub.Name,
                signature = sub.Signature,
                lineStart = sub.LineStart,
                lineEnd = sub.LineEnd,
                state = sub.State,
                commonBlockRefs = sub.CommonBlockRefs,
                calledSubroutines = sub.CalledSubroutines,
                ioPatterns = sub.IoPatterns,
                file = new
                {
                    id = file.Id,
                    relativePath = file.RelativePath,
                    lineCount = file.LineCount,
                    fileHash = file.FileHash,
                },
                corpus = new { id = corpus.Id, name = corpus.Name, state = corpus.State },
                version = new { id = version.Id, ingestedAt = version.IngestedAt },
            });
        });

        grp.MapGet("{id:guid}/source", async (Guid id, AppDbContext db, IBlobClient blob, CancellationToken ct) =>
        {
            var sub = await db.Subroutines
                .Include(s => s.SourceFile)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (sub?.SourceFile is null)
                return Results.NotFound(new { error = new { code = "subroutine.not_found" } });

            var content = await blob.GetTextAsync(sub.SourceFile.BlobUri, ct);
            return Results.Ok(new
            {
                relativePath = sub.SourceFile.RelativePath,
                fileHash = sub.SourceFile.FileHash,
                lineCount = sub.SourceFile.LineCount,
                content,
            });
        });

        return app;
    }
}
