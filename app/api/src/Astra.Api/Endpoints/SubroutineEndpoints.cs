using Astra.Api.Artefacts;
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
            string? states,
            string? lang,
            string? sort,
            string? dir,
            bool? excludeTest,
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

            // Several states at once ("IN_REVIEW,DRAFT" — the Routines page's "Needs action" view).
            var stateSet = (states ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToUpperInvariant())
                .ToList();
            if (stateSet.Count > 0)
                baseQuery = baseQuery.Where(x => stateSet.Contains(x.s.State.ToUpper()));

            if (!string.IsNullOrWhiteSpace(lang))
            {
                var langLower = lang.Trim().ToLowerInvariant();
                baseQuery = baseQuery.Where(x => x.s.SourceLanguage != null && x.s.SourceLanguage.ToLower() == langLower);
            }

            if (excludeTest == true)
            {
                // Settings are jsonb, so decide in memory over the (small) corpus list, then filter by id.
                var testIds = (await db.Corpora.AsNoTracking()
                        .Select(c => new { c.Id, c.Name, c.SettingsJson })
                        .ToListAsync(ct))
                    .Where(c => ProjectSettings.IsTestProject(c.Name, c.SettingsJson))
                    .Select(c => c.Id)
                    .ToList();
                if (testIds.Count > 0)
                    baseQuery = baseQuery.Where(x => !testIds.Contains(x.c.Id));
            }

            HashSet<Guid> specMatches = new();
            if (qUpper is not null)
            {
                // Postgres ILIKE: case-insensitive substring. Fast enough
                // for Phase C corpora; pg_trgm similarity ranking lands in
                // Phase D when the corpus list grows past ~1000 routines.
                // People search by what a routine does ("premium", "validation"), not only by its identifier: also
                // match the summary of its spec. Postgres-only jsonb read, parameterised; capped so a broad word
                // cannot build an enormous IN list.
                var pattern = $"%{qTrim}%";
                specMatches = (await db.Database
                        .SqlQuery<Guid>($"SELECT subroutine_id AS \"Value\" FROM specs WHERE spec_json->>'summary' ILIKE {pattern} LIMIT 2000")
                        .ToListAsync(ct))
                    .ToHashSet();
                var specIds = specMatches.ToList();
                baseQuery = baseQuery.Where(x =>
                    EF.Functions.ILike(x.s.Name, pattern) ||
                    EF.Functions.ILike(x.s.Signature, pattern) ||
                    EF.Functions.ILike(x.f.RelativePath, pattern) ||
                    specIds.Contains(x.s.Id));
            }

            var total = await baseQuery.CountAsync(ct);

            var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
            var ordered = (sort ?? "").ToLowerInvariant() switch
            {
                "name" => desc
                    ? baseQuery.OrderByDescending(x => x.s.Name).ThenBy(x => x.c.Name)
                    : baseQuery.OrderBy(x => x.s.Name).ThenBy(x => x.c.Name),
                "lines" => desc
                    ? baseQuery.OrderByDescending(x => x.s.LineEnd - x.s.LineStart).ThenBy(x => x.s.Name)
                    : baseQuery.OrderBy(x => x.s.LineEnd - x.s.LineStart).ThenBy(x => x.s.Name),
                "state" => desc
                    ? baseQuery.OrderByDescending(x => x.s.State).ThenBy(x => x.s.Name)
                    : baseQuery.OrderBy(x => x.s.State).ThenBy(x => x.s.Name),
                _ => desc
                    ? baseQuery.OrderByDescending(x => x.c.Name).ThenBy(x => x.f.RelativePath).ThenBy(x => x.s.LineStart)
                    : baseQuery.OrderBy(x => x.c.Name).ThenBy(x => x.f.RelativePath).ThenBy(x => x.s.LineStart),
            };

            var rows = await ordered
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
                    sourceLanguage = x.s.SourceLanguage,
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
                data = rows.Select(r => new
                {
                    r.id, r.name, r.signature, r.lineStart, r.lineEnd, r.state, r.sourceLanguage, r.file, r.corpus,
                    // True when the match came from the spec's summary rather than the name, signature or path.
                    matchedInSpec = specMatches.Contains(r.id),
                }),
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
                sourceLanguage = sub.SourceLanguage,
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
