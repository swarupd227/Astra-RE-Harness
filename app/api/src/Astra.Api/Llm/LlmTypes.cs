namespace Astra.Api.Llm;

/// <summary>
/// One unit of work emitted by an extraction pipeline. Maps directly to an
/// SSE event sent to the client.
/// </summary>
public sealed record ExtractionEvent(string Type, object Data);

public sealed record ProviderInfo(
    string Name,            // "mock" | "anthropic" | "azure_openai"
    string Model,
    string ConfigVersion);  // ZDR/no-train snapshot identity

public sealed record ExtractionRequest(
    Guid SubroutineId,
    string SubroutineName,
    string SourcePath,
    string SourceText,
    int LineCount,
    string PromptTemplateId,
    string PromptTemplateVersion);

public sealed record ExtractionResult(
    string SpecJson,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd);
