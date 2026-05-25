using System.Text.Json;

namespace Astra.Api.Llm.Dependency.Strategies;

/// <summary>
/// Default Phase 8.0.b strategy, extracted here as the canonical
/// implementation of <see cref="IPlanStrategy"/>. Pure call-graph
/// topology: Wave 1 = leaves, each subsequent wave = nodes whose
/// callees are all already placed. SCCs treated as super-nodes.
///
/// No options.
/// </summary>
public sealed class TopologicalLeavesFirstStrategy : IPlanStrategy
{
    public string Name => "topological-leaves-first";
    public string Description =>
        "Wave 1 = leaf routines (no in-corpus callees). Each later wave depends only on earlier waves. SCCs migrate together.";
    public JsonElement OptionsSchema { get; } = JsonDocument.Parse("{}").RootElement;

    public IReadOnlyList<IReadOnlyList<Guid>> AssignWaves(
        DependencyGraph graph, string optionsJson) =>
        WaveSorting.TopologicalWaves(graph);
}
