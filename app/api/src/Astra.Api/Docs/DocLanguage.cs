using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// The source language a documentation call writes about, so the writer gets that language's guidance
/// (<see cref="DocPromptAssets.LanguageBlock"/>) instead of one language's conventions for every codebase. A routine
/// call uses the routine's own language; module, overview and catalogue calls use the version's most common one.
/// </summary>
public static class DocLanguage
{
    /// <summary>The most common source language among a version's code units, or null when it has none.</summary>
    public static Task<string?> DominantAsync(AppDbContext db, Guid sourceVersionId, CancellationToken ct) =>
        db.Subroutines.AsNoTracking()
            .Where(s => s.SourceFile!.SourceVersionId == sourceVersionId)
            .GroupBy(s => s.SourceLanguage)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefaultAsync(ct);
}
