using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// Phase C — completeness check over a generated requirements pack.
///
/// The question a reviewer actually needs answered before signing is not
/// "are these requirements well written?" but "is anything MISSING?".
/// A pack that silently drops a third of the extracted business rules
/// reads exactly as well as a complete one, which is the failure mode
/// this exists to prevent.
///
/// Two coverage questions, both answerable from what the pipeline already
/// stored, plus a quality sweep:
///   1. Does every extracted business rule appear behind at least one
///      functional requirement?
///   2. Does every business capability have at least one requirement?
///   3. Which requirements lack traceability or acceptance criteria?
///
/// Matching is deliberately fuzzy. The requirement writer paraphrases —
/// a capability named "Advisor Authentication and Session Management"
/// legitimately becomes "Authentication and Session Management" on the
/// requirement — so exact string equality would report false gaps, which
/// is worse than useless: it trains the reader to ignore the report.
/// </summary>
public sealed class RequirementsCoverageService
{
    private readonly AppDbContext _db;

    public RequirementsCoverageService(AppDbContext db) => _db = db;

    public sealed record CoverageItem(string Text, bool Covered, string? CoveredBy);
    public sealed record CoverageSection(int Total, int Covered, IReadOnlyList<CoverageItem> Uncovered)
    {
        public int Percent => Total == 0 ? 100 : (int)Math.Round(100.0 * Covered / Total);
    }
    public sealed record RequirementGap(string Requirement, IReadOnlyList<string> Missing);
    public sealed record CoverageReport(
        Guid CorpusId,
        string CorpusName,
        int RequirementCount,
        int NfrCount,
        CoverageSection BusinessRules,
        CoverageSection Capabilities,
        IReadOnlyList<RequirementGap> RequirementGaps,
        bool Complete);

    public async Task<CoverageReport?> BuildAsync(Guid corpusId, CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct);
        if (corpus is null) return null;

        var kinds = new[] { "business-rule", "capability-map", "functional-requirement", "nfr" };
        var sections = await _db.DocSections.AsNoTracking()
            .Where(s => s.CorpusId == corpusId && kinds.Contains(s.SectionKind) && s.State != "REJECTED")
            .Select(s => new { s.SectionKind, s.PayloadJson })
            .ToListAsync(ct);

        var rules = sections.Where(s => s.SectionKind == "business-rule")
            .Select(s => ReadString(s.PayloadJson.RootElement, "ruleText"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        var capabilities = sections.Where(s => s.SectionKind == "capability-map")
            .Select(s => ReadString(s.PayloadJson.RootElement, "name"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        var requirements = sections.Where(s => s.SectionKind == "functional-requirement")
            .Select(s => s.PayloadJson.RootElement.Clone())
            .ToList();
        var nfrCount = sections.Count(s => s.SectionKind == "nfr");

        // Everything a requirement claims to derive from, indexed once.
        var claimedRules = requirements
            .SelectMany(r => ReadStringArray(r, "sourceRules")
                .Select(text => (Statement: ReadString(r, "statement"), Tokens: Tokenise(text))))
            .Where(x => x.Tokens.Count > 0)
            .ToList();
        var claimedCapabilities = requirements
            .Select(r => (Statement: ReadString(r, "statement"), Name: ReadString(r, "capability")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => (x.Statement, Tokens: Tokenise(x.Name)))
            .ToList();

        // Rule text is long, so a high bar is safe. Capability names are two
        // or three words, where a single differing word ("Fee Calculation and
        // Agreement" vs "Fee Management") drops the ratio below any strict
        // threshold and manufactures a gap that isn't there. Short names get
        // the lower bar; the cost of a false match is a missed gap, the cost
        // of a false gap is a report nobody believes.
        var ruleCoverage = Cover(rules, claimedRules, threshold: 0.55);
        var capabilityCoverage = Cover(capabilities, claimedCapabilities, threshold: 0.5);

        // A requirement with no traceability cannot be audited back to the
        // source, which is the property that makes this pack defensible.
        var gaps = new List<RequirementGap>();
        foreach (var r in requirements)
        {
            var missing = new List<string>();
            if (ReadStringArray(r, "sourceRoutines").Count == 0) missing.Add("traceability to source routines");
            if (ReadStringArray(r, "acceptanceCriteria").Count == 0) missing.Add("acceptance criteria");
            if (missing.Count > 0)
                gaps.Add(new RequirementGap(Truncate(ReadString(r, "statement"), 160), missing));
        }

        return new CoverageReport(
            CorpusId: corpusId,
            CorpusName: corpus.Name,
            RequirementCount: requirements.Count,
            NfrCount: nfrCount,
            BusinessRules: ruleCoverage,
            Capabilities: capabilityCoverage,
            RequirementGaps: gaps,
            Complete: ruleCoverage.Uncovered.Count == 0
                      && capabilityCoverage.Uncovered.Count == 0
                      && gaps.Count == 0);
    }

    private static CoverageSection Cover(
        IReadOnlyList<string> targets,
        IReadOnlyList<(string Statement, HashSet<string> Tokens)> claims,
        double threshold)
    {
        // Weight each word by how rare it is across both sides. Structural
        // nouns that recur everywhere ("management", "document", "proposal")
        // then carry almost no signal, while the distinguishing domain terms
        // ("restriction", "household", "fee") carry most of it. Without this,
        // two unrelated entries match on the word "management" alone — which
        // marks a real gap as covered, the one error this report must not make.
        var targetTokens = targets.Select(Tokenise).ToList();
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var set in targetTokens.Concat(claims.Select(c => c.Tokens)))
            foreach (var w in set)
                df[w] = df.GetValueOrDefault(w) + 1;

        double Weight(string w) => 1.0 / (1.0 + Math.Log(1 + df.GetValueOrDefault(w)));
        double Mass(IEnumerable<string> ws) => ws.Sum(Weight);

        double Score(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            var shared = Mass(a.Where(b.Contains));
            var denom = Math.Min(Mass(a), Mass(b));
            return denom <= 0 ? 0 : shared / denom;
        }

        var uncovered = new List<CoverageItem>();
        var covered = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var tokens = targetTokens[i];
            // Best match, not first match: at these thresholds a weak
            // coincidental overlap can otherwise win over the real counterpart
            // and get reported as the reason something is covered.
            var best = claims
                .Select(c => (c.Statement, Score: Score(tokens, c.Tokens)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (best.Score >= threshold)
                covered++;
            else
                uncovered.Add(new CoverageItem(Truncate(targets[i], 200), false, null));
        }
        return new CoverageSection(targets.Count, covered, uncovered);
    }

    // Words that carry no discriminating signal in requirement prose and
    // would otherwise let any two sentences look alike.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "then", "that", "this", "with", "from", "for", "must", "shall",
        "are", "was", "were", "has", "have", "had", "not", "any", "all", "its",
        "system", "when", "which", "into", "onto", "out", "per", "via", "each",
    };

    private static HashSet<string> Tokenise(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new HashSet<string>(StringComparer.Ordinal);
        return Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]{3,}")
            .Select(m => Singularise(m.Value))
            .Where(t => !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Crude plural folding, enough that "Account Restriction Management"
    /// and "Account Restrictions" are recognised as the same capability.
    /// Deliberately not a real stemmer: over-stemming merges unrelated
    /// domain terms, and a false match hides a gap.
    /// </summary>
    private static string Singularise(string token)
    {
        if (token.Length <= 4) return token;
        if (token.EndsWith("ies", StringComparison.Ordinal)) return token[..^3] + "y";
        if (token.EndsWith("ses", StringComparison.Ordinal)) return token[..^2];
        if (token.EndsWith("s", StringComparison.Ordinal) && !token.EndsWith("ss", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    private static string ReadString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static List<string> ReadStringArray(JsonElement el, string prop)
    {
        var list = new List<string>();
        if (el.ValueKind != JsonValueKind.Object) return list;
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
