using System.Text.Json;
using Astra.Api.Evidence;

namespace Astra.Api.Knowledge;

/// <summary>
/// Checks claims against what the structured evidence says, with no model: each claim is matched to the things in the
/// entity graph it names and then compared with what the graph states about them. Three outcomes, by rule:
/// <list type="bullet">
/// <item><b>Supported</b> — every assertion is stated by structured evidence (a link, or an attribute with that value);</item>
/// <item><b>Conflict</b> — a structured source states the opposite (the other order of two jobs, a different value) and
/// that source's evidence exists, so the disagreement can be shown with both sides;</item>
/// <item><b>Unverified</b> — nothing structured says either way: a thing not in the inventory, nothing stated about it,
/// or a claim with no checkable assertion.</item>
/// </list>
/// A claim is a Conflict if any assertion conflicts, Supported only if every assertion is and there is at least one.
/// </summary>
public static class ClaimLinker
{
    public static IReadOnlyList<ClaimVerdict> Link(IReadOnlyList<Claim> claims, EntityGraphDto graph, IEvidenceLookup evidence)
    {
        var byRef = graph.Nodes.GroupBy(n => n.Ref, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = graph.Nodes.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges;
        return claims.Select(c => Judge(c, byRef, byName, edges, evidence)).ToList();
    }

    private sealed record Part(string Status, string Reason, IReadOnlyList<string> Evidence, IReadOnlyList<string> Entities);

    private static ClaimVerdict Judge(
        Claim claim,
        IReadOnlyDictionary<string, GraphNode> byRef,
        IReadOnlyDictionary<string, List<GraphNode>> byName,
        IReadOnlyList<GraphEdge> edges,
        IEvidenceLookup evidence)
    {
        if (claim.Assertions.Count == 0)
            return new ClaimVerdict(claim.Id, StatementStatus.Unverified, "Nothing in it can be checked against code, SQL, a scheduler or configuration.", Array.Empty<string>(), Array.Empty<string>());

        var parts = claim.Assertions.Select(a => JudgePart(a, claim, byRef, byName, edges, evidence)).ToList();
        var entities = parts.SelectMany(p => p.Entities).Distinct(StringComparer.Ordinal).ToList();

        var conflict = parts.FirstOrDefault(p => p.Status == StatementStatus.Conflict);
        if (conflict is not null)
            return new ClaimVerdict(claim.Id, StatementStatus.Conflict, conflict.Reason, conflict.Evidence, entities);

        if (parts.All(p => p.Status == StatementStatus.Supported))
            return new ClaimVerdict(claim.Id, StatementStatus.Supported, parts[0].Reason, parts.SelectMany(p => p.Evidence).Distinct(StringComparer.Ordinal).ToList(), entities);

        var open = parts.First(p => p.Status != StatementStatus.Supported);
        return new ClaimVerdict(claim.Id, StatementStatus.Unverified, open.Reason, parts.SelectMany(p => p.Evidence).Distinct(StringComparer.Ordinal).ToList(), entities);
    }

    private static Part JudgePart(
        ClaimAssertion a,
        Claim claim,
        IReadOnlyDictionary<string, GraphNode> byRef,
        IReadOnlyDictionary<string, List<GraphNode>> byName,
        IReadOnlyList<GraphEdge> edges,
        IEvidenceLookup evidence)
    {
        var (subject, subjectProblem) = Resolve(a.Subject, byRef, byName);
        if (subject is null) return Open(subjectProblem!);

        if (a.Predicate == ClaimPredicates.Attribute)
        {
            var attribute = (a.Object ?? "").Trim();
            if (attribute.Length == 0 || string.IsNullOrWhiteSpace(a.Value)) return Open($"The claim about {subject.Name} does not say which attribute has which value.", subject);
            var values = AttributeValues(subject, attribute);
            if (values.Count == 0) return Open($"No structured source states the {attribute} of {subject.Name}.", subject);
            var claimed = Norm(a.Value!);
            if (values.Any(v => Norm(v) == claimed))
                return new Part(StatementStatus.Supported, $"The {attribute} of {subject.Name} is {a.Value} in structured evidence.", subject.EvidenceIds, new[] { subject.Ref });
            return Disagree(
                $"The claim says the {attribute} of {subject.Name} is {a.Value}; structured evidence says {string.Join(" / ", values.Distinct())}.",
                subject.EvidenceIds, claim, evidence, subject);
        }

        if (!EntityVocabulary.Relations.ContainsKey(a.Predicate)) return Open($"'{a.Predicate}' is not a relation that can be checked.", subject);
        var (other, otherProblem) = Resolve(a.Object ?? "", byRef, byName);
        if (other is null) return Open(otherProblem!, subject);

        var forward = edges.Where(e => e.Relation == a.Predicate && Same(e.From, subject.Ref) && Same(e.To, other.Ref)).ToList();
        if (forward.Count > 0)
            return new Part(StatementStatus.Supported,
                $"Structured evidence shows {subject.Name} {EntityVocabulary.Relations[a.Predicate]} {other.Name}.",
                forward.SelectMany(e => e.EvidenceIds).Distinct(StringComparer.Ordinal).ToList(), new[] { subject.Ref, other.Ref });

        // Only an order can be contradicted by its reverse; "reads" or "writes" that nothing states is merely unconfirmed.
        if (a.Predicate == EntityVocabulary.Precedes)
        {
            var reverse = edges.Where(e => e.Relation == a.Predicate && Same(e.From, other.Ref) && Same(e.To, subject.Ref)).ToList();
            if (reverse.Count > 0)
                return Disagree(
                    $"The claim says {subject.Name} runs before {other.Name}; structured evidence has {other.Name} before {subject.Name}.",
                    reverse.SelectMany(e => e.EvidenceIds).Distinct(StringComparer.Ordinal).ToList(), claim, evidence, subject, other);
        }

        return Open($"No structured source says that {subject.Name} {EntityVocabulary.Relations[a.Predicate]} {other.Name}, or the opposite.", subject, other);
    }

    private static Part Open(string reason, params GraphNode[] nodes) =>
        new(StatementStatus.Unverified, reason, nodes.SelectMany(n => n.EvidenceIds).Distinct(StringComparer.Ordinal).ToList(), nodes.Select(n => n.Ref).ToList());

    /// <summary>A conflict is real only with two distinct pieces of evidence that exist: the claim's own and a structured one.</summary>
    private static Part Disagree(string reason, IReadOnlyList<string> structured, Claim claim, IEvidenceLookup evidence, params GraphNode[] nodes)
    {
        var real = structured.Where(id => !string.Equals(id, claim.EvidenceId, StringComparison.Ordinal) && evidence.Find(id) is not null)
            .Distinct(StringComparer.Ordinal).ToList();
        var entities = nodes.Select(n => n.Ref).ToList();
        if (real.Count == 0 || evidence.Find(claim.EvidenceId) is null)
            return new Part(StatementStatus.Unverified, reason + " (The disagreement is not recorded: it needs evidence on both sides.)", structured, entities);
        return new Part(StatementStatus.Conflict, reason, real, entities);
    }

    private static (GraphNode? Node, string? Problem) Resolve(string name, IReadOnlyDictionary<string, GraphNode> byRef, IReadOnlyDictionary<string, List<GraphNode>> byName)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "The claim does not name the thing it is about.");
        if (byRef.TryGetValue(name, out var exact)) return (exact, null);
        if (!byName.TryGetValue(name, out var candidates)) return (null, $"{name} is not in the inventory.");
        if (candidates.Count == 1) return (candidates[0], null);
        var declared = candidates.Where(c => c.Declared).ToList();
        if (declared.Count == 1) return (declared[0], null);
        return (null, $"{name} could be any of: {string.Join(", ", candidates.Select(c => c.Ref))}.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Norm(string s) => string.Join(' ', s.Trim().Trim('"', '\'', '`').ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// What an entity states for an attribute, as text: the attribute itself, or — for a setting that has a value per
    /// file — each file's value. An attribute that is a list of objects contributes each object's <c>value</c>.
    /// </summary>
    internal static IReadOnlyList<string> AttributeValues(GraphNode node, string attribute)
    {
        if (node.Attrs is not { ValueKind: JsonValueKind.Object } attrs) return Array.Empty<string>();
        var found = new List<string>();
        foreach (var name in new[] { attribute, attribute + "s" })
        {
            foreach (var p in attrs.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                Collect(p.Value, found);
            if (found.Count > 0) break;
        }
        return found;
    }

    private static void Collect(JsonElement e, List<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: into.Add(e.GetString() ?? ""); break;
            case JsonValueKind.Number: case JsonValueKind.True: case JsonValueKind.False: into.Add(e.ToString()); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Collect(x, into); break;
            case JsonValueKind.Object:
                if (e.TryGetProperty("value", out var v)) Collect(v, into);
                break;
        }
    }
}

/// <summary>Evidence ids known to exist in a version, without their text: all a conflict needs.</summary>
public sealed class IdSetEvidence : IEvidenceLookup
{
    private readonly IReadOnlySet<string> _ids;

    public IdSetEvidence(IReadOnlySet<string> ids) { _ids = ids; }

    public GateChunk? Find(string shortId) =>
        _ids.Contains(shortId) ? new GateChunk(shortId, "", "", shortId, "") : null;
}
