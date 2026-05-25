using System.Text.Json;

namespace Astra.Api.Llm.Dependency.Strategies;

/// <summary>
/// Pilot-then-scale strategy. Wave 1 is an explicitly-chosen pilot
/// set (caller-supplied routine names). Waves 2-N are standard
/// topological-leaves-first from there.
///
/// Why this is useful: the team picks 3-8 representative routines for
/// the FIRST migration sprint to prove out the toolchain + the SME
/// review loop. After Wave 1 lands, the rest of the corpus follows
/// the normal topology.
///
/// Topological correctness caveat: the pilot routines may have
/// unmigrated callees in later waves. That's a deliberate choice the
/// team is making about their pilot. The platform doesn't block it.
///
/// Options shape (JSON):
/// {
///   "pilotRoutineNames": ["CONSUME_ROLL", "INV_READ", "EMIT_EVENT"]
/// }
/// </summary>
public sealed class PilotThenScaleStrategy : IPlanStrategy
{
    public string Name => "pilot-then-scale";
    public string Description =>
        "Wave 1 = caller-chosen pilot routines (regardless of topology). Waves 2+ follow standard topological-leaves-first.";
    public JsonElement OptionsSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "pilotRoutineNames": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Routine names (case-insensitive) to put in Wave 1. Typically 3-8 routines."
            }
          },
          "required": ["pilotRoutineNames"]
        }
        """).RootElement;

    public IReadOnlyList<IReadOnlyList<Guid>> AssignWaves(
        DependencyGraph graph, string optionsJson)
    {
        var pilotNames = ParsePilotNames(optionsJson);
        if (pilotNames.Count == 0)
        {
            // No pilot specified → fall through to plain topological.
            return WaveSorting.TopologicalWaves(graph);
        }

        var nameToId = graph.Nodes
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var pilotIds = pilotNames
            .Where(n => nameToId.ContainsKey(n))
            .Select(n => nameToId[n])
            .ToHashSet();

        return WaveSorting.ConstrainedTopologicalWaves(graph, pilotIds, comparer: null);
    }

    private static IReadOnlyList<string> ParsePilotNames(string optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson) || optionsJson == "{}") return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (!doc.RootElement.TryGetProperty("pilotRoutineNames", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var list = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
