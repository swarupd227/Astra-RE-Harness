namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>Bound from <c>Llm:Survey</c>.</summary>
public sealed class SurveyOptions
{
    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxOutputTokens { get; set; } = 600;
    /// <summary>Parallel survey calls. The process-wide
    /// <see cref="AnthropicRateLimiter"/> still gates the real ceiling.</summary>
    public int Concurrency { get; set; } = 32;
    /// <summary>Routines longer than this are sent as head + tail with an
    /// elision marker; the digest only needs the routine's shape.</summary>
    public int LineCap { get; set; } = 400;
    public int HeadLines { get; set; } = 300;
    public int TailLines { get; set; } = 100;
}

public sealed record SurveyRequest(
    Guid SubroutineId,
    string Name,
    string Signature,
    string SourceLanguage,
    string SourcePath,
    IReadOnlyList<string> Callees,
    int CallerCount,
    int LineStart,
    int LineEnd,
    /// <summary>Line-numbered slice of the routine (already capped).</summary>
    string LineSlice);

public sealed record SurveyDataAccess(string Table, string Op);

public sealed record SurveyDigest(
    string Purpose,
    IReadOnlyList<string> ClaimKinds,
    string? ArchetypeHint,
    IReadOnlyList<SurveyDataAccess> DataAccess,
    IReadOnlyList<string> ModernizationFlags,
    string Complexity);

public sealed record SurveyResult(
    SurveyDigest Digest,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    string Model,
    string PromptId,
    string PromptVersion,
    long LatencyMs);

/// <summary>
/// The cheap per-routine digest call. One implementation talks to Claude
/// (Haiku by default); the mock keeps the whole pattern-analysis pipeline
/// runnable offline, as <c>MockLlmProvider</c> does for extraction.
/// </summary>
public interface ISurveyProvider
{
    string Name { get; }
    Task<SurveyResult> SurveyAsync(SurveyRequest request, CancellationToken ct);
}

public static class SurveyVocabulary
{
    public static readonly string[] ArchetypeHints =
    {
        "trivial-accessor", "constructor-or-init", "rest-resource-handler", "ui-event-handler",
        "data-access-query", "data-access-write", "batch-file-processor", "report-generator",
        "calculation", "validation", "orchestration", "state-machine", "string-utility",
        "io-adapter", "network-client", "conversion-or-mapping", "error-handling", "other",
    };

    public static readonly string[] ModernizationFlags =
    {
        "legacy-component", "ui-logic-mixed", "data-access-inline", "dead-code-candidate",
        "duplicate-logic", "error-handling-weak", "hardcoded-config", "reporting-legacy",
        "network-legacy", "concurrency-risk", "global-state",
    };

    public static readonly string[] Complexities = { "trivial", "simple", "moderate", "complex" };
}
