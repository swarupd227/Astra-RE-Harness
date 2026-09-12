namespace Astra.Api.Llm;

/// <summary>
/// USD cost estimate per call, resolved from the model that actually ran —
/// not the configured default. The previous flat $3/$15 (Sonnet) estimate
/// billed Haiku survey/trivial-tier calls at 3× their real price, so no
/// dashboard could ever show the savings from tiering.
///
/// Anthropic's <c>usage.input_tokens</c> excludes cached tokens; cache reads
/// and cache writes are reported separately and priced at 10% / 125% of the
/// model's input rate.
/// </summary>
public static class ModelPricing
{
    // USD per million tokens. Prefix-matched so dated snapshots
    // ("claude-sonnet-4-5-20250929") resolve without an entry per snapshot.
    // Most specific prefixes first.
    private static readonly (string Prefix, decimal InputPerM, decimal OutputPerM)[] Table =
    {
        ("claude-haiku-4-5", 1m, 5m),
        ("claude-haiku", 0.8m, 4m),
        ("claude-opus-4-1", 15m, 75m),
        ("claude-opus-4-0", 15m, 75m),
        ("claude-opus-4-20", 15m, 75m),
        ("claude-opus", 5m, 25m),
        ("claude-sonnet", 3m, 15m),
    };

    private const decimal CacheReadMultiplier = 0.10m;
    private const decimal CacheWriteMultiplier = 1.25m;

    public static (decimal InputPerM, decimal OutputPerM) Resolve(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var row in Table)
        {
            if (m.StartsWith(row.Prefix, StringComparison.Ordinal))
                return (row.InputPerM, row.OutputPerM);
        }
        // Unknown Claude model: keep the historical Sonnet assumption.
        return (3m, 15m);
    }

    public static decimal Estimate(
        string provider,
        string? model,
        int inputTokens,
        int outputTokens,
        int cacheReadTokens = 0,
        int cacheCreationTokens = 0)
    {
        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase)) return 0m;
        var (inRate, outRate) = Resolve(model);
        var usd =
            inputTokens * inRate
            + cacheReadTokens * inRate * CacheReadMultiplier
            + cacheCreationTokens * inRate * CacheWriteMultiplier
            + outputTokens * outRate;
        return Math.Round(usd / 1_000_000m, 4);
    }
}
