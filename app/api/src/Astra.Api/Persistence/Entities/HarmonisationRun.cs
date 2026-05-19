namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Phase 7.1 — One execution of the cross-routine harmonisation pass
/// over a corpus's signed specs. The pipeline loads every SIGNED spec
/// in the corpus's latest version, sends them to the LLM with a
/// "find inconsistencies" prompt, and persists the LLM's findings as
/// child <see cref="HarmonisationFinding"/> rows.
///
/// Why this is a separate pass from per-routine extraction:
///   - Phase 7.0 gives each per-routine extract a "neighbourhood" of
///     up to a dozen related routines. That's enough for local
///     dependencies but cannot catch corpus-wide inconsistencies
///     ("INV_READ is described as 'VSAM READ' in spec A but as
///     'in-memory cache lookup' in spec B").
///   - A harmonisation pass takes all specs at once (typically <500
///     routines * ~2k tokens each = 1M tokens, well within Claude's
///     200k input with chunking-or-streaming if needed) and flags
///     contradictions. The output is structured findings — NOT
///     spec rewrites; the SME decides which spec is correct.
/// </summary>
public sealed class HarmonisationRun
{
    public Guid Id { get; set; }
    public Guid CorpusId { get; set; }
    public Guid SourceVersionId { get; set; }

    /// <summary>"RUNNING" | "COMPLETED" | "FAILED".</summary>
    public string Status { get; set; } = "RUNNING";

    /// <summary>Prompt id + version pinned at run time (audit trail).</summary>
    public string PromptId { get; set; } = "";
    public string PromptVersion { get; set; } = "";

    /// <summary>Model that ran the pass.</summary>
    public string ModelName { get; set; } = "";

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }

    /// <summary>Total specs the run analysed.</summary>
    public int SpecCount { get; set; }

    /// <summary>Total findings produced.</summary>
    public int FindingCount { get; set; }

    /// <summary>One-line summary for the run-history list view.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Optional error message when Status = FAILED.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Triggered-by persona display name (audit trail).</summary>
    public string? TriggeredBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

/// <summary>
/// One structured inconsistency the harmonisation pass found across
/// the corpus's specs. The findings are SUGGESTIONS — the SME confirms
/// or rejects each, optionally with a follow-up review of the affected
/// spec(s).
/// </summary>
public sealed class HarmonisationFinding
{
    public Guid Id { get; set; }
    public Guid HarmonisationRunId { get; set; }

    /// <summary>
    /// "callee_io_drift"          — same callee, different IO claims across specs.
    /// "common_layout_drift"      — same COMMON block, contradictory field orders.
    /// "terminology_drift"        — same concept named differently (CUSTOMER vs CLIENT).
    /// "missing_invariant"        — invariant present in caller's spec but absent from callee's.
    /// "duplicate_open_question"  — the same magic constant raised in multiple specs.
    /// "uncategorised"            — the LLM produced a finding that didn't match a known category.
    /// </summary>
    public string Category { get; set; } = "uncategorised";

    /// <summary>"low" | "medium" | "high".</summary>
    public string Severity { get; set; } = "medium";

    /// <summary>Short title (≤80 chars). Shown in the findings list.</summary>
    public string Title { get; set; } = "";

    /// <summary>Markdown body. Includes the contradictory claims with
    /// references to each affected spec id.</summary>
    public string Detail { get; set; } = "";

    /// <summary>JSON array of affected spec ids (Guids). Lets the UI
    /// link directly to each spec from the finding card.</summary>
    public string AffectedSpecIdsJson { get; set; } = "[]";

    /// <summary>"open" | "accepted" | "dismissed". Admin-curated state.</summary>
    public string Status { get; set; } = "open";

    /// <summary>Optional admin note added when Status moves to
    /// accepted/dismissed (an audit-trail hook).</summary>
    public string? AdminNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
