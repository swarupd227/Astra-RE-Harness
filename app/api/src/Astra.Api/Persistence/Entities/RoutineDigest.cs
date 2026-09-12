namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Phase 15.2 — the cheap per-routine digest that pattern analysis clusters
/// on, instead of a full signable spec. Produced by the survey tier (Haiku,
/// a few hundred output tokens, the routine's own line slice only), or
/// propagated from a structurally identical exemplar, or derived from an
/// existing Spec when one is already present. Persisted so a run that dies
/// mid-way resumes by skipping what is already here, and so an Assessment
/// can count patterns/data-access/modernization flags without ever paying
/// for extraction.
/// </summary>
public sealed class RoutineDigest
{
    public Guid Id { get; set; }
    public Guid SubroutineId { get; set; }
    public Guid SourceVersionId { get; set; }

    /// <summary>"survey" | "spec" | "propagated" | "trivial".</summary>
    public string Source { get; set; } = "survey";

    public string Purpose { get; set; } = "";
    public string? ArchetypeHint { get; set; }

    /// <summary>JSON array of claim-kind keys present, e.g. ["invariant","objectLifetime"].</summary>
    public string ClaimKindsJson { get; set; } = "[]";

    /// <summary>JSON array of {table, op} the routine appears to touch, when detectable.</summary>
    public string? DataAccessJson { get; set; }

    /// <summary>JSON array of modernization-flag strings (legacy-construct, ui-logic, dead-code…).</summary>
    public string? ModernizationFlagsJson { get; set; }

    /// <summary>"trivial" | "simple" | "moderate" | "complex".</summary>
    public string? Complexity { get; set; }

    /// <summary>SHA-256 of the identifier-normalised token stream — equal
    /// for routines that are the same code modulo names/comments/whitespace.</summary>
    public string? StructuralHash { get; set; }
    public int NormalizedTokenCount { get; set; }

    /// <summary>When Source == "propagated": the routine whose survey digest this copies.</summary>
    public Guid? ExemplarSubroutineId { get; set; }

    public string? PromptVersion { get; set; }
    public string? Model { get; set; }
    public Guid? LlmCallId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Subroutine? Subroutine { get; set; }
}
