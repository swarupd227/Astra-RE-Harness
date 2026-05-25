using System.Text.Json;

namespace Astra.Api.Llm.Dependency.Strategies;

/// <summary>
/// Business-priority strategy. Caller provides an explicit priority
/// for each routine (lower number = higher priority); the strategy
/// runs topological assignment within each priority bucket.
///
/// Options shape (JSON):
/// {
///   "priorities": {
///     "ROUTINE_NAME_1": 1,
///     "ROUTINE_NAME_2": 1,
///     "ROUTINE_NAME_3": 2
///   },
///   "defaultPriority": 99
/// }
///
/// Routines whose names appear in <c>priorities</c> get that priority;
/// others get <c>defaultPriority</c> (default 99 = "after everything
/// the customer named"). Within a priority bucket the topological
/// wave assignment is the standard 8.0.b Kahn BFS. Across buckets,
/// lower priority numbers produce earlier waves.
/// </summary>
public sealed class BusinessPriorityStrategy : IPlanStrategy
{
    public string Name => "business-priority";
    public string Description =>
        "Caller-supplied priority per routine; topological assignment within each priority bucket.";
    public JsonElement OptionsSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "priorities": {
              "type": "object",
              "description": "Routine name (case-insensitive) → priority integer (lower = earlier).",
              "additionalProperties": { "type": "integer" }
            },
            "defaultPriority": {
              "type": "integer",
              "description": "Priority for routines not listed (default 99).",
              "default": 99
            }
          }
        }
        """).RootElement;

    public IReadOnlyList<IReadOnlyList<Guid>> AssignWaves(
        DependencyGraph graph, string optionsJson)
    {
        var options = ParseOptions(optionsJson);

        // Map name → priority (case-insensitive).
        var nameToPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, p) in options.Priorities) nameToPriority[n] = p;
        var defaultPri = options.DefaultPriority;

        // Routine id → priority bucket.
        var idToBucket = new Dictionary<Guid, int>();
        foreach (var n in graph.Nodes)
        {
            idToBucket[n.Id] = nameToPriority.TryGetValue(n.Name, out var p) ? p : defaultPri;
        }
        var buckets = idToBucket.Values.Distinct().OrderBy(b => b).ToList();

        // For each bucket: build a sub-graph containing only its nodes
        // + their edges (drop cross-bucket edges so the planner doesn't
        // wait on routines from a different bucket). Then assign waves
        // bucket-by-bucket; concatenate.
        var allWaves = new List<List<Guid>>();
        foreach (var bucket in buckets)
        {
            var bucketIds = idToBucket
                .Where(kv => kv.Value == bucket)
                .Select(kv => kv.Key)
                .ToHashSet();
            var subgraph = SubGraphFor(graph, bucketIds);
            var bucketWaves = WaveSorting.TopologicalWaves(subgraph);
            foreach (var w in bucketWaves)
                if (w.Count > 0) allWaves.Add(w);
        }
        return allWaves;
    }

    private static DependencyGraph SubGraphFor(DependencyGraph graph, HashSet<Guid> ids)
    {
        var nodes = graph.Nodes.Where(n => ids.Contains(n.Id)).ToList();
        var edges = graph.Edges
            .Where(e => ids.Contains(e.From) && ids.Contains(e.To))
            .ToList();
        var sccs = graph.Sccs
            .Where(s => s.Members.All(m => ids.Contains(m)))
            .ToList();
        // Stats not used by the wave-sorter; pass through empty.
        return new DependencyGraph(
            CorpusId: graph.CorpusId,
            SourceVersionId: graph.SourceVersionId,
            Nodes: nodes,
            Edges: edges,
            ExternalCallees: graph.ExternalCallees,
            Sccs: sccs,
            Stats: graph.Stats);
    }

    private static ParsedOptions ParseOptions(string optionsJson)
    {
        var defaults = new ParsedOptions(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 99);
        if (string.IsNullOrWhiteSpace(optionsJson) || optionsJson == "{}") return defaults;
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            var root = doc.RootElement;
            var prios = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("priorities", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pEl.EnumerateObject())
                    if (prop.Value.TryGetInt32(out var v)) prios[prop.Name] = v;
            }
            var def = 99;
            if (root.TryGetProperty("defaultPriority", out var dEl) && dEl.TryGetInt32(out var dv)) def = dv;
            return new ParsedOptions(prios, def);
        }
        catch
        {
            return defaults;
        }
    }

    private sealed record ParsedOptions(IDictionary<string, int> Priorities, int DefaultPriority);
}
