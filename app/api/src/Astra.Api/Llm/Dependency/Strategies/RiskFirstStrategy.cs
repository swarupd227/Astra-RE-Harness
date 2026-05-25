using System.Text.Json;

namespace Astra.Api.Llm.Dependency.Strategies;

/// <summary>
/// Risk-first strategy. Within the topological order, routines with
/// larger blast radius (more transitive callers) sort earlier within
/// each wave. The wave assignment itself still respects call-graph
/// topology — you cannot migrate a routine ahead of its callees.
///
/// Rationale: high-blast-radius routines need the most SME attention,
/// and the SME review queue should reach them first. Within a wave
/// of true leaves (Wave 1), the leaf with the largest downstream
/// footprint goes first; the SME reviews it before lower-impact
/// leaves.
///
/// No options.
/// </summary>
public sealed class RiskFirstStrategy : IPlanStrategy
{
    public string Name => "risk-first";
    public string Description =>
        "Topological waves, but within each wave the highest-blast-radius routines sort first (SME attention priority).";
    public JsonElement OptionsSchema { get; } = JsonDocument.Parse("{}").RootElement;

    public IReadOnlyList<IReadOnlyList<Guid>> AssignWaves(
        DependencyGraph graph, string optionsJson)
    {
        // Compute blast radius (= transitive caller count) per node.
        var reverseCall = new Dictionary<Guid, List<Guid>>();
        foreach (var n in graph.Nodes) reverseCall[n.Id] = new List<Guid>();
        foreach (var e in graph.Edges)
        {
            if (e.Type != "call") continue;
            if (reverseCall.TryGetValue(e.To, out var list)) list.Add(e.From);
        }
        var blastById = new Dictionary<Guid, int>();
        foreach (var n in graph.Nodes)
            blastById[n.Id] = TransitiveSize(reverseCall, n.Id);

        // Comparer: larger blast first; ties by id for determinism.
        var comparer = Comparer<Guid>.Create((a, b) =>
        {
            var ba = blastById.GetValueOrDefault(a);
            var bb = blastById.GetValueOrDefault(b);
            if (ba != bb) return bb.CompareTo(ba); // descending
            return a.CompareTo(b);
        });

        return WaveSorting.ConstrainedTopologicalWaves(graph, Array.Empty<Guid>(), comparer);
    }

    private static int TransitiveSize(Dictionary<Guid, List<Guid>> reverseCall, Guid start)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!visited.Add(node)) continue;
            if (reverseCall.TryGetValue(node, out var ups))
                foreach (var u in ups) if (!visited.Contains(u)) stack.Push(u);
        }
        visited.Remove(start); // don't count the routine itself
        return visited.Count;
    }
}
