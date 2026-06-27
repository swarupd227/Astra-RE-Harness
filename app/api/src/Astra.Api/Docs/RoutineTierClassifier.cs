using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.a — picks a routine's model tier (utility | mid | headline) for
/// the doc-summary pipeline. Tier drives which model handles the routine
/// (Haiku batched / Sonnet / Opus) and how much of the prompt context budget
/// it gets.
///
/// We deliberately DO NOT depend on Phase 8.0.c's BlastRadius service: that
/// path requires a fully-loaded dependency graph per routine and a
/// MigrationPlan row to materialise readiness, neither of which an SME
/// should have to do before generating documentation. Instead we derive
/// importance from two cheap, per-corpus signals already on the Subroutine
/// rows:
///
///   1. **Reverse-call count.** How many routines in the same corpus call
///      this one (read from <c>Subroutine.CalledSubroutines</c> jsonb).
///      Load-bearing routines tend to be called from many places.
///   2. **LOC (LineEnd − LineStart + 1).** Big routines tend to carry more
///      logic and need a fuller summary.
///
/// Thresholds were calibrated against LAPACK BLAS during the 11.0 slice;
/// they will be revisited when 11.0.h's E2E demo run gives us a per-tier
/// quality + cost breakdown.
/// </summary>
public sealed class RoutineTierClassifier
{
    private readonly AppDbContext _db;

    public RoutineTierClassifier(AppDbContext db) => _db = db;

    public sealed record TierAssignment(string Tier, int CallerCount, int Loc);

    public async Task<IReadOnlyDictionary<Guid, TierAssignment>> ClassifyForCorpusAsync(
        Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        var subs = await _db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
            .ToListAsync(ct);

        // Reverse-call count. Subroutine.Name is not unique across files
        // (think Fortran SUBROUTINE F in two files) so we count by name and
        // accept the noise — over-counting a clashed name pushes that routine
        // up a tier, which is harmless. Under-counting is the failure mode
        // we'd care about.
        var callerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in subs)
        {
            if (sub.CalledSubroutines is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(sub.CalledSubroutines.RootElement.GetRawText());
                foreach (var callee in EnumerateNames(doc.RootElement))
                {
                    callerCounts.TryGetValue(callee, out var existing);
                    callerCounts[callee] = existing + 1;
                }
            }
            catch { /* malformed jsonb on a single row is non-fatal */ }
        }

        var result = new Dictionary<Guid, TierAssignment>(subs.Count);
        foreach (var sub in subs)
        {
            var calls = callerCounts.TryGetValue(sub.Name, out var c) ? c : 0;
            var loc = sub.LineEnd > sub.LineStart ? (sub.LineEnd - sub.LineStart + 1) : 0;
            var tier = PickTier(calls, loc);
            result[sub.Id] = new TierAssignment(tier, calls, loc);
        }
        return result;
    }

    private static string PickTier(int callerCount, int loc)
    {
        // Thresholds calibrated against LAPACK Reference BLAS (154 routines)
        // during the 11.0.a verification pass: the initial loose rules
        // promoted 30% of routines to headline, blowing the cost band on
        // Opus. The current rules target ~5% headline / ~25% mid / ~70%
        // utility on a typical scientific-library corpus.
        //
        // Headline: load-bearing AND substantial. A 500-LOC leaf is
        // probably dead code; a 20-LOC routine called from 100 places is
        // probably a one-line wrapper.
        if (callerCount >= 25 && loc >= 80) return "headline";
        if (loc >= 400) return "headline";

        // Mid: meaningful complexity OR meaningful reach.
        if (callerCount >= 8 || loc >= 120) return "mid";

        return "utility";
    }

    private static IEnumerable<string> EnumerateNames(JsonElement element)
    {
        // CalledSubroutines may be either a flat array of strings or an
        // array of objects ({name, ...}). Tolerate both shapes.
        if (element.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in element.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) yield return s;
                    break;
                case JsonValueKind.Object:
                    if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        var name = n.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) yield return name;
                    }
                    break;
            }
        }
    }
}
