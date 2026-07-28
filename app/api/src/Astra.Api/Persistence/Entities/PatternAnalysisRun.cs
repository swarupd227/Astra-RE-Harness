namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Phase 12.0 — One execution of the pattern-analysis pass over a corpus:
/// (1) bulk-extract every un-extracted subroutine's spec, then (2) bucket
/// every spec's claims by kind and ask the LLM to judge which routines
/// actually share a behavioural pattern, producing <see cref="PatternCluster"/>
/// rows with a suggested archetype name per cluster.
///
/// Why this exists: per-routine extraction (Phase 3b) and cross-routine
/// harmonisation (Phase 7.1, contradiction-finding across ALREADY-SIGNED
/// specs) both answer different questions. This pass answers "how many
/// DISTINCT recipes does this codebase actually contain, and which
/// routines share one" — the question that determines how many archetypes
/// a real migration engagement needs to build.
/// </summary>
public sealed class PatternAnalysisRun
{
    public Guid Id { get; set; }
    public Guid CorpusId { get; set; }
    public Guid SourceVersionId { get; set; }

    /// <summary>Always "extract,cluster" today; kept as a string (not an
    /// enum) so a future stage can be added without a schema change,
    /// mirroring DocGenerationRun.StagesRequested.</summary>
    public string StagesRequested { get; set; } = "extract,cluster";

    /// <summary>"QUEUED" | "RUNNING" | "SUCCEEDED" | "PARTIAL" | "FAILED".</summary>
    public string State { get; set; } = "QUEUED";

    /// <summary>Incrementally-updated progress: extraction counts (succeeded/
    /// failed/skipped/total) and, once stage 2 starts, bucket/cluster counts
    /// and LLM token usage. Shape is informal (UI reads it defensively).</summary>
    public string? MetricsJson { get; set; }

    public string Summary { get; set; } = "";

    public string? ErrorSummary { get; set; }

    /// <summary>Triggered-by persona display name (audit trail, matches
    /// HarmonisationRun's convention rather than DocGenerationRun's unused
    /// Guid? field).</summary>
    public string? TriggeredBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Corpus? Corpus { get; set; }
}

/// <summary>
/// One behavioural pattern the clustering pass identified: a group of
/// subroutines whose specs the LLM judged similar enough to share ONE
/// hand-built archetype. Singleton clusters (a claim-kind-signature
/// bucket with exactly one member) are still recorded — they represent
/// the "long tail" of one-off routines a real engagement would handle
/// case-by-case rather than building a dedicated archetype for.
/// </summary>
public sealed class PatternCluster
{
    public Guid Id { get; set; }
    public Guid PatternAnalysisRunId { get; set; }
    public Guid CorpusId { get; set; }

    /// <summary>Deterministic hint computed BEFORE the LLM call: the sorted,
    /// comma-joined set of claim kinds present across this cluster's specs
    /// (e.g. "dynamicArrayUsage,invariant,recordAccessSemantics"). The LLM
    /// is free to split or merge across this hint — it is not authoritative,
    /// just the coarse candidate grouping the prompt was built from.</summary>
    public string ClaimKindSignature { get; set; } = "";

    /// <summary>Short human-readable name for the pattern, e.g. "Multivalue
    /// list check-then-insert".</summary>
    public string Label { get; set; } = "";

    /// <summary>A kebab-case archetype id suggestion, e.g.
    /// "canonical-unibasic-list-insert-service". Not auto-created — a human
    /// still designs and verifies the actual archetype, same as the two
    /// archetypes built by hand earlier this engagement.</summary>
    public string SuggestedArchetypeName { get; set; } = "";

    /// <summary>The LLM's reasoning for why these routines share (or, for a
    /// singleton, do not yet have evidence to share) one pattern.</summary>
    public string Rationale { get; set; } = "";

    /// <summary>JSON array of {subroutineId, subroutineName, specId}.</summary>
    public string MemberSubroutineIdsJson { get; set; } = "[]";

    public int MemberCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
