namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.a — strongly-typed options for the documentation generator.
/// Bound from configuration section "Docs:Generator". Per-tier model
/// overrides let the long-tail Haiku batch path share infrastructure with
/// the headline-routine Opus path without per-call wiring.
/// </summary>
public sealed class DocsOptions
{
    /// <summary>"mock" | "anthropic". Mock is for tests; never used in prod.</summary>
    public string Provider { get; set; } = "mock";

    /// <summary>Model ID for the utility tier (batched, long tail).</summary>
    public string HaikuModel { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>Model ID for the mid tier (single-shot, ~25% of routines).</summary>
    public string SonnetModel { get; set; } = "claude-sonnet-4-5-20250929";

    /// <summary>Model ID for the headline tier (single-shot, top ~5%).</summary>
    public string OpusModel { get; set; } = "claude-opus-4-5";

    /// <summary>Routines per Haiku batch call. 20 is a safe default at ~8 lines/routine.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Bounded concurrency for Anthropic calls. 4 keeps us comfortably below rate limits.</summary>
    public int MaxConcurrency { get; set; } = 4;
}
