using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Picks a routine's model tier (headline | standard) for the doc pipelines.
/// Tier drives which model handles the routine (Opus for headline, Sonnet
/// for standard); files that contain a headline routine get their module
/// document on Opus too.
///
/// Importance score = (callerCount × 3 + loc / 5) × complexity weight. The
/// multipliers weight reach (how many routines call this one) more heavily
/// than raw size, since a large leaf routine is less load-bearing than a
/// small widely-called one. Calibrated on LAPACK BLAS: with
/// HeadlinePercentile=10 the top tier captures DGEMM, DTRSM and the main
/// BLAS-3 kernels.
///
/// WS6: when the pattern-analysis survey has left a <see cref="RoutineDigest"/>
/// with a complexity rating, the score is weighted by it — complex ×1.5,
/// moderate ×1.0, simple ×0.75 — and trivial routines (accessors,
/// one-line wrappers) are never promoted to the headline tier however many
/// callers they have. Corpora without digests classify exactly as before.
///
/// CallerNames are included so the pipelines can pass them into prompts.
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
        IReadOnlyList<string> CallerNames,
        string? Complexity = null);

    public static double ComplexityWeight(string? complexity) => (complexity ?? "").ToLowerInvariant() switch
    {
        "complex" => 1.5,
        "moderate" => 1.0,
        "simple" => 0.75,
        "trivial" => 0.0,
        _ => 1.0,
    };

    public async Task<IReadOnlyDictionary<Guid, TierAssignment>> ClassifyForCorpusAsync(
        Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        var subs = await _db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
            .ToListAsync(ct);

        // Survey complexity, when the pattern-analysis survey has run.
        var digestRows = await _db.RoutineDigests
            .AsNoTracking()
            .Where(d => d.SourceVersionId == sourceVersionId && d.Complexity != null)
            .Select(d => new { d.SubroutineId, d.Complexity })
            .ToListAsync(ct);
        var complexity = new Dictionary<Guid, string>();
        foreach (var d in digestRows)
            complexity[d.SubroutineId] = d.Complexity!;

        // Reverse-call maps: callee name → caller names / count. Subroutine.Name
        // is not unique across files, so this counts by name and accepts the noise.
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

        var scored = subs.Select(sub =>
        {
            var calls = callerCountMap.TryGetValue(sub.Name, out var c) ? c : 0;
            var loc = sub.LineEnd > sub.LineStart ? sub.LineEnd - sub.LineStart + 1 : 0;
            var cx = complexity.TryGetValue(sub.Id, out var cxv) ? cxv : null;
            var weight = ComplexityWeight(cx);
            var score = (calls * 3 + loc / 5) * weight;
            var eligible = weight > 0;
            return (sub, calls, loc, cx, score, eligible);
        }).ToList();

        // Top HeadlinePercentile % → Opus ("headline"); rest → Sonnet ("standard").
        var pct = Math.Clamp(_opts.HeadlinePercentile, 1, 50);
        var headlineCount = Math.Max(1, (int)Math.Ceiling(scored.Count * pct / 100.0));
        var eligibleScored = scored.Where(x => x.eligible).ToList();
        var pool = eligibleScored.Count > 0 ? eligibleScored : scored;
        var headlineIds = pool
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.loc)
            .Take(headlineCount)
            .Select(x => x.sub.Id)
            .ToHashSet();

        var result = new Dictionary<Guid, TierAssignment>(subs.Count);
        foreach (var (sub, calls, loc, cx, _, _) in scored)
        {
            var tier = headlineIds.Contains(sub.Id) ? "headline" : "standard";
            var callerNames = callerNamesMap.TryGetValue(sub.Name, out var cn)
                ? (IReadOnlyList<string>)cn
                : Array.Empty<string>();
            result[sub.Id] = new TierAssignment(tier, calls, loc, callerNames, cx);
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
