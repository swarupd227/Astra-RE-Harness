using System.Text.Json;

namespace Astra.Api.Persistence.Entities;

/// <summary>
/// In-app notification. Phase C.7 surfaces @-mention dispatches; Phase D
/// will fan-out additional types (spec-routed, signed, scaffold-ready).
///
/// Recipient is identified by persona for now (engineer | sme | observer |
/// admin) — when OIDC ships in Phase D this becomes a real user id and
/// persona is reduced to a derived display attribute.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; set; }
    public string RecipientPersona { get; set; } = "";

    /// <summary>e.g. <c>"comment.mention"</c>, <c>"spec.routed"</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>e.g. <c>"comment"</c>, <c>"spec"</c>, <c>"claim"</c>.</summary>
    public string TargetType { get; set; } = "";
    public Guid TargetId { get; set; }

    /// <summary>Free-form payload: spec id, claim path, author display, body excerpt, …</summary>
    public JsonDocument Payload { get; set; } = JsonDocument.Parse("{}");

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>Persona that *caused* the notification — for inbox grouping.</summary>
    public string? ActorPersona { get; set; }
    public string? ActorDisplay { get; set; }
}
