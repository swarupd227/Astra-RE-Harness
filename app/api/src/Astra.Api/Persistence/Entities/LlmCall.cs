namespace Astra.Api.Persistence.Entities;

public sealed class LlmCall
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";       // "mock" | "anthropic" | "azure_openai"
    public string Model { get; set; } = "";
    public string PromptTemplateId { get; set; } = "";
    public string PromptTemplateVersion { get; set; } = "";

    /// <summary>
    /// Snapshot of the residency-relevant config (e.g. ZDR endpoint flags).
    /// "mock:offline" for the deterministic mock provider.
    /// </summary>
    public string ProviderConfigVersion { get; set; } = "";

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public long LatencyMs { get; set; }
    public decimal CostUsd { get; set; }

    public string Status { get; set; } = "success"; // success | failure | cancelled
    public string? ErrorCode { get; set; }

    public Guid? CalledBy { get; set; }
    public DateTimeOffset CalledAt { get; set; }
}
