using System.Text.RegularExpressions;

namespace Astra.Api.Knowledge;

/// <param name="EvidenceId">The ticket's evidence id; <paramref name="Reference"/> is what people call it (<c>INC-T-0412</c>).</param>
public sealed record TicketText(string EvidenceId, string Reference, string Text);

/// <param name="Key">The error code or name the tickets share (<c>2627</c>), else <c>cluster-N</c>.</param>
/// <param name="Candidate">Three or more incidents: worth a known-error article.</param>
public sealed record TicketCluster(string Key, IReadOnlyList<string> EvidenceIds, IReadOnlyList<string> References, IReadOnlyList<string> SharedIdentifiers, bool Candidate);

/// <param name="Routine">Tickets that are routine requests (a password, a printer): kept out of the known-error step.</param>
public sealed record TicketClustering(IReadOnlyList<TicketCluster> Clusters, IReadOnlyList<string> Routine);

/// <summary>
/// Groups tickets that describe the same problem, with no model and no embeddings: two tickets belong together when
/// they share an error code, or share a name (a job, a table) and enough of their words, or are nearly the same
/// words. The result is deterministic, so a cluster is a fact a person can check. Routine requests are tagged by keyword
/// rules and never clustered.
/// </summary>
public static class TicketClusterer
{
    public const int DefaultMinSize = 3;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "that", "this", "with", "from", "have", "been", "were", "they", "their", "there", "when", "then", "than", "will",
        "would", "could", "should", "after", "before", "again", "which", "while", "about", "into", "onto", "over", "also",
        "please", "thanks", "ticket", "user", "reported", "customer", "issue", "problem",
    };

    private sealed record Features(HashSet<string> Words, HashSet<string> Codes, HashSet<string> Names);

    public static TicketClustering Cluster(IReadOnlyList<TicketText> tickets, IReadOnlyList<string> routineKeywords, int minSize = DefaultMinSize)
    {
        var routine = new List<string>();
        var kept = new List<(TicketText Ticket, Features F)>();
        foreach (var t in tickets.OrderBy(t => t.EvidenceId, StringComparer.Ordinal))
        {
            var f = Featurise(t.Text);
            if (f.Codes.Count == 0 && f.Names.Count == 0 && IsRoutine(ValuesOnly(t.Text), routineKeywords)) routine.Add(t.EvidenceId);
            else kept.Add((t, f));
        }

        var parent = Enumerable.Range(0, kept.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (var i = 0; i < kept.Count; i++)
            for (var j = i + 1; j < kept.Count; j++)
                if (Similar(kept[i].F, kept[j].F)) parent[Find(j)] = Find(i);

        var groups = Enumerable.Range(0, kept.Count).GroupBy(Find).Select(g => g.Select(i => kept[i]).ToList()).ToList();
        var clusters = new List<TicketCluster>();
        foreach (var g in groups.OrderByDescending(g => g.Count).ThenBy(g => g[0].Ticket.EvidenceId, StringComparer.Ordinal))
        {
            var shared = g.SelectMany(x => x.F.Codes).Concat(g.SelectMany(x => x.F.Names))
                .GroupBy(s => s, StringComparer.Ordinal).Where(x => g.Count == 1 || x.Count() > 1)
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToList();
            var codeKey = g.SelectMany(x => x.F.Codes).GroupBy(s => s, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();
            var key = codeKey ?? shared.FirstOrDefault() ?? $"cluster-{clusters.Count + 1}";
            clusters.Add(new TicketCluster(
                key,
                g.Select(x => x.Ticket.EvidenceId).ToList(),
                g.Select(x => x.Ticket.Reference).ToList(),
                shared,
                g.Count >= Math.Max(1, minSize)));
        }
        return new TicketClustering(clusters, routine);
    }

    /// <summary>A ticket reads <c>column: value</c> per line; the column names (<c>opened_at</c>) are the same in every ticket and say nothing about the problem.</summary>
    public static string ValuesOnly(string text) =>
        Regex.Replace(text, @"(?m)^[A-Za-z0-9_ .\-]{1,40}:[ \t]+", "");

    private static Features Featurise(string text)
    {
        text = ValuesOnly(text);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in IdentifierExtractor.Extract(text))
        {
            var n = IdentifierExtractor.Normalise(id.Text);
            // A ticket's own number is not what the problem is about.
            if (Regex.IsMatch(id.Text, @"^[A-Z]{2,5}(?:-[A-Z0-9]{1,4})*-\d+$")) continue;
            if (id.Kind == IdentifierKind.Code) codes.Add(Regex.Match(id.Text, @"\d[\d.\-]*").Value is { Length: > 0 } d ? d : n);
            else names.Add(n);
        }
        var words = Regex.Matches(text.ToLowerInvariant(), @"[a-z]{4,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).ToHashSet(StringComparer.Ordinal);
        return new Features(words, codes, names);
    }

    private static bool Similar(Features a, Features b)
    {
        if (a.Codes.Overlaps(b.Codes)) return true;
        var jaccard = Jaccard(a.Words, b.Words);
        if (a.Names.Overlaps(b.Names) && jaccard >= 0.25) return true;
        return jaccard >= 0.5;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static bool IsRoutine(string text, IReadOnlyList<string> keywords) =>
        keywords.Any(k => !string.IsNullOrWhiteSpace(k) && Regex.IsMatch(text, @"\b" + Regex.Escape(k.Trim()) + @"\b", RegexOptions.IgnoreCase));
}
