namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.a — strongly-typed options for the documentation generator.
/// Bound from configuration section "Docs:Generator".
///
/// Quality upgrade (Phase 11.0.f): Haiku batch tier removed. All routines
/// run single-shot. Top <see cref="HeadlinePercentile"/> percent by composite
/// importance score (caller count + LOC) go to Opus; remainder to Sonnet.
/// This eliminates the ~200-token-per-routine ceiling that produced shallow
/// utility-tier output and lets every routine receive a full 4096-token budget.
/// </summary>
public sealed class DocsOptions
{
    /// <summary>"mock" | "anthropic". Mock is for tests; never used in prod.</summary>
    public string Provider { get; set; } = "mock";

    /// <summary>Model ID for the standard tier (single-shot, bottom 90% by importance).</summary>
    public string SonnetModel { get; set; } = "claude-sonnet-4-6";

    /// <summary>Model ID for the headline tier (single-shot, top HeadlinePercentile %).</summary>
    public string OpusModel { get; set; } = "claude-opus-4-8";

    /// <summary>
    /// Percentage of routines (by composite importance score) that receive Opus.
    /// Default 10 = top 10%. Raise to 20 for smaller corpora where most routines matter.
    /// </summary>
    public int HeadlinePercentile { get; set; } = 10;

    /// <summary>Bounded concurrency for Anthropic calls. 6 works well with single-shot Sonnet/Opus.</summary>
    public int MaxConcurrency { get; set; } = 6;
}
