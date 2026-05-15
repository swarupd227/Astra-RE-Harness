namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Post-migration validation pass against a generated scaffold.
/// Each row records one stage's outcome — COMPILE, TEST_PACK, or
/// EQUIVALENCE — so the report card can show three independent badges
/// and so re-runs are an append, not a mutation.
///
/// State machine:
///     RUNNING → PASSED        (build/tests/equivalence all green)
///     RUNNING → FAILED        (any failure; ErrorSummary explains)
///     RUNNING → ERRORED       (infra problem; pass is inconclusive)
/// </summary>
public sealed class ValidationRun
{
    public Guid Id { get; set; }
    public Guid ScaffoldId { get; set; }
    public Guid SpecId { get; set; }

    /// <summary>"COMPILE" | "TEST_PACK" | "EQUIVALENCE".</summary>
    public string Stage { get; set; } = "COMPILE";

    /// <summary>"RUNNING" | "PASSED" | "FAILED" | "ERRORED".</summary>
    public string Status { get; set; } = "RUNNING";

    /// <summary>Short displayable summary — e.g. "Build succeeded · 0 warnings".</summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// MinIO blob URI of the full build log / test report. Optional —
    /// not every stage produces a log (or it may not exist yet while RUNNING).
    /// </summary>
    public string? LogBlobUri { get; set; }

    /// <summary>Machine-readable failure reason; null when status=PASSED.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Per-stage metric payload (warnings/errors/passed/failed counts).</summary>
    public string? MetricsJson { get; set; }

    public Guid? StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Scaffold? Scaffold { get; set; }
    public Spec? Spec { get; set; }
}
