using System.Text.Json;

namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Append-only audit log entry. Per spec §8.4 the trail is never edited or
/// deleted — purges only happen at the partition level after the 7-year
/// retention window (Phase D).
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }

    /// <summary>Stable, hierarchical event identifier — e.g. "spec.signed".</summary>
    public string EventType { get; set; } = "";

    public Guid? ActorId { get; set; }
    /// <summary>Persona at the time of the action ("engineer" / "sme" / ...).</summary>
    public string ActorPersona { get; set; } = "";
    public string ActorDisplay { get; set; } = "";

    /// <summary>e.g. "spec" / "subroutine" / "corpus" / "scaffold".</summary>
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }

    /// <summary>Free-form structured payload (diffs, metadata).</summary>
    public JsonDocument Payload { get; set; } = JsonDocument.Parse("{}");

    public DateTimeOffset OccurredAt { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
