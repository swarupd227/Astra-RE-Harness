namespace Astra.Api.Llm;

/// <summary>
/// Anthropic provider configuration. Bound from <c>Llm:Anthropic</c> in
/// configuration. The API key is read from <c>ANTHROPIC_API_KEY</c> via
/// the standard env var binding (<c>Llm__Anthropic__ApiKey</c>).
/// </summary>
public sealed class AnthropicOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-5-20250929";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiVersion { get; set; } = "2023-06-01";
    // Phase 10.2.a — bumped from 8192. VB6's dotnet10-blazor extract
    // prompt + 9-claim-kind schema regularly exhausted the old cap on
    // mid-sized routines, returning truncated JSON that AnthropicLlmProvider
    // couldn't parse. 16384 buys ~2x headroom while staying well under
    // Sonnet 4.5's 64k output ceiling.
    public int MaxOutputTokens { get; set; } = 16384;

    /// <summary>
    /// Snapshot of the residency-relevant config — recorded on every
    /// <see cref="Persistence.Entities.LlmCall"/> row so auditors can
    /// reconstruct exactly which retention regime applied per call.
    /// </summary>
    public string ConfigVersion { get; set; } =
        "anthropic:zdr=true:no-train:no-retention:enterprise-endpoint";
}
