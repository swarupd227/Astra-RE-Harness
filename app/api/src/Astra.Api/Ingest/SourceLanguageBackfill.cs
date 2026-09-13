using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Ingest;

/// <summary>
/// Re-stamp <c>Subroutine.SourceLanguage</c> from the file extension where
/// the two disagree. Re-sync never set the column, so every re-synced
/// routine sat at the entity default "fortran-f77" — extracted with the
/// Fortran schema, priced at Fortran throughput, described as Fortran in
/// the assessment. Runs at boot after the schema step; the extension
/// mapping is the same one ingest uses, so this is a repair, not a guess.
/// </summary>
public static class SourceLanguageBackfill
{
    public static async Task<int> ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.Subroutines
            .AsNoTracking()
            .Select(s => new { s.Id, Path = s.SourceFile!.RelativePath, s.SourceLanguage })
            .ToListAsync(ct);

        var fixes = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var detected = SourceLanguageDetector.FromFilename(r.Path);
            if (detected is null || detected == r.SourceLanguage) continue;
            if (!fixes.TryGetValue(detected, out var ids)) fixes[detected] = ids = new List<Guid>();
            ids.Add(r.Id);
        }

        var total = 0;
        foreach (var (language, ids) in fixes)
        {
            foreach (var chunk in ids.Chunk(1000))
            {
                var batch = chunk.ToArray();
                total += await db.Subroutines
                    .Where(s => batch.Contains(s.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceLanguage, language), ct);
            }
            Serilog.Log.Warning(
                "Source-language backfill: {Count} routine(s) re-stamped as {Language} (their files' extensions say so; the rows carried another language)",
                ids.Count, language);
        }

        return total;
    }
}
