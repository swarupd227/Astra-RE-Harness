using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.a — picks a routine's model tier (headline | standard) for
/// the doc-summary pipeline. Tier drives which model handles the routine
/// (Opus for headline, Sonnet for standard).
///
/// Phase 11.0.f quality upgrade: Haiku/batch/utility tier removed entirely.
/// All routines now run single-shot; the only question is Opus vs. Sonnet.
/// The top <see cref="DocsOptions.HeadlinePercentile"/> percent by composite
/// importance score go to Opus; the rest get Sonnet.
///
/// Importance score = callerCount × 3 + (loc / 5). The multipliers weight
/// reach (how many routines call this one) more heavily than raw size, since
/// a large but leaf-level routine is less load-bearing than a small but
/// widely-called one. Calibrated on LAPACK BLAS: with HeadlinePercentile=10
/// the top tier captures DGEMM, DTRSM, and the main BLAS-3 kernels.
///
/// CallerNames are included in TierAssignment so the pipeline can pass them
/// into the doc-summary prompt as additional context, helping the model
/// understand which higher-level routines depend on this one.
/// </summary>
public sealed class RoutineTierClassifier
{
    private readonly AppDbContext _db;
    private readonly DocsOptions _opts;

    public RoutineTierClassifier(AppDbContext db, IOptions<DocsOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public sealed record TierAssignment(
        string Tier,
        int CallerCount,
        int Loc,
        IReadOnlyList<string> CallerNames);

    public async Task<IReadOnlyDictionary<Guid, TierAssignment>> ClassifyForCorpusAsync(
        Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        var subs = await _db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
            .ToListAsync(ct);

        // Build reverse-call maps: callee name → caller names, and callee name → caller count.
        // Subroutine.Name is not unique across files (Fortran allows duplicate names in
        // different source units) so we count by name and accept the noise.
        var callerCountMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var callerNamesMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in subs)
        {
            if (sub.CalledSubroutines is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(sub.CalledSubroutines.RootElement.GetRawText());
                foreach (var callee in EnumerateNames(doc.RootElement))
                {
                    callerCountMap.TryGetValue(callee, out var existing);
                    callerCountMap[callee] = existing + 1;

                    if (!callerNamesMap.TryGetValue(callee, out var list))
                        callerNamesMap[callee] = list = [];
                    if (!list.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                        list.Add(sub.Name);
                }
            }
            catch { /* malformed jsonb on a single row is non-fatal */ }
        }

        // Compute composite importance score for each routine.
        var scored = subs.Select(sub =>
        {
            var calls = callerCountMap.TryGetValue(sub.Name, out var c) ? c : 0;
            var loc = sub.LineEnd > sub.LineStart ? sub.LineEnd - sub.LineStart + 1 : 0;
            var score = calls * 3 + loc / 5;
            return (sub, calls, loc, score);
        }).ToList();

        // Top HeadlinePercentile % → Opus ("headline"); rest → Sonnet ("standard").
        var pct = Math.Clamp(_opts.HeadlinePercentile, 1, 50);
        var headlineCount = Math.Max(1, (int)Math.Ceiling(scored.Count * pct / 100.0));
        var headlineIds = scored
            .OrderByDescending(x => x.score)
            .Take(headlineCount)
            .Select(x => x.sub.Id)
            .ToHashSet();

        var result = new Dictionary<Guid, TierAssignment>(subs.Count);
        foreach (var (sub, calls, loc, _) in scored)
        {
            var tier = headlineIds.Contains(sub.Id) ? "headline" : "standard";
            var callerNames = callerNamesMap.TryGetValue(sub.Name, out var cn)
                ? (IReadOnlyList<string>)cn
                : Array.Empty<string>();
            result[sub.Id] = new TierAssignment(tier, calls, loc, callerNames);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateNames(JsonElement element)
    {
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
