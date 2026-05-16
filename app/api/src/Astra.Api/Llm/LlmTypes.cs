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
    string PromptTemplateVersion,
    // Phase 5.2 — language + target stack drive prompt-library routing
    // inside the provider. Default values keep pre-5.2 call sites
    // compiling without explicit migration.
    string SourceLanguage = "fortran-f77",
    string TargetStack = "dotnet8");

public sealed record ExtractionResult(
    string SpecJson,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd);
