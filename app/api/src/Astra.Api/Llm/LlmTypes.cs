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

/// <summary>
/// Per-call tuning for a bulk extraction pass. Interactive extraction
/// passes null and keeps the configured defaults.
/// </summary>
public sealed record ExtractionTuning(
    string? ModelOverride = null,
    int? MaxOutputTokensOverride = null,
    bool BulkMode = false);

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
    string TargetStack = "dotnet8",
    // Phase 7.0 — structured cross-routine context. Null when the
    // caller hasn't built one (legacy per-routine extraction); a
    // populated value enables the "## Neighbourhood" block in the
    // prompt and is what lets the extract surface cross-file
    // dependencies without RAG or 200k stuffing.
    Neighbourhood? Neighbourhood = null,
    // Bulk-run tuning. Wall-clock on a bulk pass is dominated by output
    // generation, so the lever that matters is routing trivial routines
    // (one-line accessors and the like) to a faster model. Null means
    // "use the configured default".
    string? ModelOverride = null,
    int? MaxOutputTokensOverride = null,
    // True when nobody is watching a live stream: skips the deliberate
    // pacing delays that exist to make the interactive UI readable.
    bool BulkMode = false);

public sealed record ExtractionResult(
    string SpecJson,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd);
