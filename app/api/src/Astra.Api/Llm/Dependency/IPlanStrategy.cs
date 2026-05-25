using System.Text.Json;

namespace Astra.Api.Llm.Dependency;

/// <summary>
/// Phase 8.0.e — Pluggable migration-plan strategies. Every strategy
/// turns a <see cref="DependencyGraph"/> into an ordered list of waves.
///
/// Strategies are registered in DI and looked up by <see cref="Name"/>
/// at plan-generation time. The set of strategies is open — customers
/// can add their own implementations and register them alongside the
/// built-ins.
///
/// Built-ins (this PR):
///   - topological-leaves-first  — pure call-graph topology.
///   - business-priority         — caller-supplied priority buckets,
///                                 topological within each bucket.
///   - risk-first                — high-blast-radius routines first
///                                 (SME attention priority).
///   - pilot-then-scale          — explicit pilot routines in Wave 1,
///                                 topological from Wave 2 onwards.
/// </summary>
public interface IPlanStrategy
{
    /// <summary>Stable kebab-case identifier used in the API and persisted
    /// on <c>MigrationPlan.StrategyName</c>.</summary>
    string Name { get; }

    /// <summary>Human-readable one-line description shown in the strategy
    /// picker. Audience: the engineer or admin choosing a strategy.</summary>
    string Description { get; }

    /// <summary>JSON schema describing this strategy's accepted options.
    /// Empty <c>{}</c> for strategies that take no options. Used by the
    /// UI to render an options form.</summary>
    JsonElement OptionsSchema { get; }

    /// <summary>
    /// Run the assignment. Returns an ordered list of waves, each a
    /// list of routine ids. The first wave must be at index 0. Within a
    /// wave the order is the strategy's choice (typically alphabetical
    /// for determinism).
    /// </summary>
    /// <param name="graph">Phase 8.0.a dependency graph for the corpus.</param>
    /// <param name="optionsJson">Strategy-specific options as JSON.
    /// Empty <c>{}</c> when caller didn't supply any.</param>
    IReadOnlyList<IReadOnlyList<Guid>> AssignWaves(
        DependencyGraph graph,
        string optionsJson);
}
