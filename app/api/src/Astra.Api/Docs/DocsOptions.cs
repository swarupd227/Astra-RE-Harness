namespace Astra.Api.Docs;

/// <summary>
/// Strongly-typed options for the documentation generator. Bound from
/// configuration section "Docs:Generator".
///
/// Two model tiers remain (Phase 11.0.f): the top <see cref="HeadlinePercentile"/>
/// percent of routines by importance go to <see cref="OpusModel"/>, the rest to
/// <see cref="SonnetModel"/>. Module documents whose file contains a headline
/// routine, and the system overview, are written on the Opus tier too.
///
/// WS6 (documentation quality): <see cref="Provider"/> now selects the
/// <see cref="IDocWriter"/> — "mock" returns deterministic envelopes offline,
/// "anthropic" calls the Messages API through the shared retrying send and the
/// process-wide rate limiter. The input budget, source-slice cap, output
/// ceilings and the critic loop are configurable below. The old
/// <c>HaikuModel</c> / <c>BatchSize</c> keys were never read and are gone.
/// </summary>
public sealed class DocsOptions
{
    /// <summary>"mock" | "anthropic". Selects the writer for every documentation
    /// stage. Anything other than "anthropic" is treated as mock.</summary>
    public string Provider { get; set; } = "mock";

    /// <summary>Model for the standard tier (routines outside the headline
    /// percentile, modules without a headline routine, catalogs, the critic).</summary>
    public string SonnetModel { get; set; } = "claude-sonnet-4-6";

    /// <summary>Model for the headline tier (top routines, modules that contain
    /// one, and the system overview).</summary>
    public string OpusModel { get; set; } = "claude-opus-4-8";

    /// <summary>
    /// Percentage of routines (by composite importance score, complexity-
    /// weighted when a survey digest exists) that receive Opus. Default 10.
    /// </summary>
    public int HeadlinePercentile { get; set; } = 10;

    /// <summary>Bounded concurrency per stage. The process-wide
    /// <see cref="Llm.AnthropicRateLimiter"/> still gates every call.</summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>
    /// Ceiling on a single writer prompt's user message, in tokens estimated
    /// at four characters per token. When a message would exceed it, whole
    /// routines are dropped — lowest tier and smallest first — never chopped
    /// to a character count.
    /// </summary>
    public int InputBudgetTokens { get; set; } = 150_000;

    /// <summary>Line-numbered source lines per routine shown to the module and
    /// business-rules writers. Longer routines are cut at this many lines
    /// with a note; the summary still covers the rest.</summary>
    public int ModuleSourceLinesPerRoutine { get; set; } = 400;

    public int ModuleMaxOutputTokens { get; set; } = 8192;
    public int OverviewMaxOutputTokens { get; set; } = 16384;
    public int CatalogMaxOutputTokens { get; set; } = 16384;

    /// <summary>Run the critic → revise loop after each module / overview
    /// draft. Deterministic checks run regardless.</summary>
    public bool CriticEnabled { get; set; } = true;

    /// <summary>Model for the critic call. Empty = <see cref="SonnetModel"/>.</summary>
    public string? CriticModel { get; set; }

    /// <summary>Critic total (four axes × 1–5) below which one revise call is
    /// issued. A failed deterministic check also triggers the revise.</summary>
    public int CriticThreshold { get; set; } = 16;

    public int CriticMaxOutputTokens { get; set; } = 4096;

    public bool IsMock => !string.Equals(Provider, "anthropic", StringComparison.OrdinalIgnoreCase);
}
