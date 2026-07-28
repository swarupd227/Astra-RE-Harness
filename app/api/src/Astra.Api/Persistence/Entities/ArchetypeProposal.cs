namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Phase 14.0 — a new archetype proposed by the LLM from a
/// <see cref="PatternCluster"/>'s member specs, instead of hand-authored
/// on disk. Closes the loop Pattern Analysis opened: a cluster with no
/// matching archetype can be turned into one from inside the running
/// app, with no code change or container restart, mirroring the same
/// draft-then-verify-then-approve shape every other artifact in this
/// platform goes through (a spec's DRAFT → IN_REVIEW → SIGNED; here,
/// DRAFT → VERIFIED or VERIFICATION_FAILED → PRODUCTION or REJECTED).
///
/// Once approved, <see cref="Llm.Archetypes.ArchetypeRegistry"/> registers
/// it live in its in-memory index — no restart needed. On the next boot,
/// the registry also reloads every PRODUCTION proposal from this table
/// (in addition to walking the filesystem), so approved archetypes
/// survive a restart without ever having been written to disk.
/// </summary>
public sealed class ArchetypeProposal
{
    public Guid Id { get; set; }
    public Guid PatternClusterId { get; set; }
    public Guid CorpusId { get; set; }
    public string TargetStack { get; set; } = "";

    /// <summary>Phase 15.0.a — the schema id (source language) the cluster's
    /// member routines were extracted from, e.g. "java" or "unibasic".
    /// Stamped onto the live-registered archetype's compatibleSchemas so
    /// PickForSubroutine only ever matches this archetype against routines
    /// from the same source language — without it, every live-authored
    /// archetype was hardcoded to "unibasic" regardless of what corpus it
    /// was actually authored from.</summary>
    public string SourceSchema { get; set; } = "";

    /// <summary>Kebab-case archetype id the LLM proposed, e.g.
    /// "canonical-unibasic-batch-export-sftp-trigger".</summary>
    public string ProposedArchetypeId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>JSON array of subroutine names this archetype should match
    /// (becomes archetype.json's matches.anyOf at registration time).</summary>
    public string MatchesJson { get; set; } = "[]";

    /// <summary>JSON array of {path, language, content, derivedFromClaimIds}
    /// — the complete proposed package (pom.xml + main + test sources).</summary>
    public string FilesJson { get; set; } = "[]";

    /// <summary>"DRAFT" | "VERIFICATION_FAILED" | "VERIFIED" | "PRODUCTION" | "REJECTED".</summary>
    public string State { get; set; } = "DRAFT";

    public string? CompileLog { get; set; }
    public int? CompileErrorCount { get; set; }
    public int? TestCount { get; set; }
    public int? TestFailureCount { get; set; }

    public Guid? LlmCallId { get; set; }
    public string? GeneratedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? RejectedReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
