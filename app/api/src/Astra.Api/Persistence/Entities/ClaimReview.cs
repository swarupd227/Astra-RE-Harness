namespace Astra.Api.Persistence.Entities;

public sealed class ClaimReview
{
    public Guid Id { get; set; }
    public Guid SpecId { get; set; }

    /// <summary>JSONPath into the spec, e.g. "$.invariants[?(@.id=='INV-1')]".</summary>
    public string ClaimPath { get; set; } = "";

    /// <summary>"accept" | "edit" | "reject" | "question".</summary>
    public string Action { get; set; } = "";

    /// <summary>Required for "reject" (≥20 chars) and "question".</summary>
    public string? Reason { get; set; }

    /// <summary>Required for "edit" — the new claim text.</summary>
    public string? EditedText { get; set; }

    public Guid? ReviewerId { get; set; }
    public DateTimeOffset ReviewedAt { get; set; }

    public Spec? Spec { get; set; }
}
