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

        var ruleCoverage = Cover(rules, claimedRules, threshold: 0.55);
        var capabilityCoverage = Cover(capabilities, claimedCapabilities, threshold: 0.6);

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
        var uncovered = new List<CoverageItem>();
        var covered = 0;
        foreach (var target in targets)
        {
            var tokens = Tokenise(target);
            var hit = claims.FirstOrDefault(c => Similar(tokens, c.Tokens, threshold));
            if (hit.Tokens is not null)
            {
                covered++;
            }
            else
            {
                uncovered.Add(new CoverageItem(Truncate(target, 200), false, null));
            }
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
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Containment rather than Jaccard: a requirement legitimately cites a
    /// shortened form of a rule, so the smaller set being largely inside
    /// the larger one is the signal, not equal size.
    /// </summary>
    private static bool Similar(HashSet<string> a, HashSet<string> b, double threshold)
    {
        if (a.Count == 0 || b.Count == 0) return false;
        var overlap = a.Count(b.Contains);
        return (double)overlap / Math.Min(a.Count, b.Count) >= threshold;
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
