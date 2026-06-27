namespace Astra.Api.Persistence.Entities;

/// <summary>
/// One invocation of the documentation generator over a corpus. Mirrors
/// the shape of <see cref="ValidationRun"/> so cost / model / latency are
/// auditable per run and so re-runs are append-only.
///
/// Generation runs span multiple stages (per ADR-038 §11.0.a–c): per-routine
/// summaries → module rollups → corpus-level overview + catalogues +
/// diagrams. Each stage records its own metrics; <see cref="State"/>
/// reflects the overall run.
///
/// State machine:
///     QUEUED   → RUNNING        (worker picked it up)
///     RUNNING  → SUCCEEDED      (all requested stages emitted at least
///                                one DocSection without infra error)
///     RUNNING  → PARTIAL        (some stages succeeded, some hit the
///                                long-tail LLM-error budget; produced
///                                sections still land)
///     RUNNING  → FAILED         (infra failure; produced sections roll
///                                back; ErrorSummary explains)
/// </summary>
public sealed class DocGenerationRun
{
    public Guid Id { get; set; }
    public Guid CorpusId { get; set; }
    public Guid SourceVersionId { get; set; }

    /// <summary>
    /// Comma-separated stages requested for this run, in execution order.
    /// e.g. "routine-summary,module,glossary,data-dictionary,overview".
    /// Stored as a single string to keep the DDL one column; parsed on
    /// read. Subsetting is supported (a run can re-emit only one stage).
    /// </summary>
    public string StagesRequested { get; set; } = "";

    /// <summary>"QUEUED" | "RUNNING" | "SUCCEEDED" | "PARTIAL" | "FAILED".</summary>
    public string State { get; set; } = "QUEUED";

    /// <summary>
    /// Counts per stage and per model tier (haiku / sonnet / opus).
    /// Shape (rough):
    ///   {
    ///     "routine-summary": { "sections": 412, "tier": { "haiku": 320, "sonnet": 80, "opus": 12 }, "inputTokens": 1.4e6, "outputTokens": 220_000, "usdCents": 1830 },
    ///     "module":           { ... },
    ///     ...
    ///   }
    /// </summary>
    public string? MetricsJson { get; set; }

    /// <summary>Short human-readable summary surfaced in the run list.</summary>
    public string Summary { get; set; } = "";

    public string? ErrorCode { get; set; }
    public string? ErrorSummary { get; set; }

    public Guid? TriggeredBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Corpus? Corpus { get; set; }
}
