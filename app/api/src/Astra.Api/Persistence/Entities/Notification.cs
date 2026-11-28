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

    /// <summary>
    /// Set when the notification is for one person ("user:&lt;id&gt;", see <c>InboxScope</c>) rather than a whole persona —
    /// e.g. a comment on a spec they follow. Null = the persona's inbox.
    /// </summary>
    public string? RecipientUserKey { get; set; }

    /// <summary>Persona that *caused* the notification — for inbox grouping.</summary>
    public string? ActorPersona { get; set; }
    public string? ActorDisplay { get; set; }
}

/// <summary>One person's "I have read this" for a notification (password sign-in; personas keep <see cref="Notification.ReadAt"/>).</summary>
public sealed class NotificationRead
{
    public Guid NotificationId { get; set; }
    public string UserKey { get; set; } = "";
    public DateTimeOffset ReadAt { get; set; }
}

/// <summary>Someone following a spec: every new comment on it reaches their inbox, not only @-mentions.</summary>
public sealed class SpecFollow
{
    public Guid SpecId { get; set; }
    /// <summary>"user:&lt;id&gt;" or "persona:&lt;persona&gt;" (see <c>InboxScope</c>).</summary>
    public string UserKey { get; set; } = "";
    /// <summary>The follower's persona, which addresses the notification.</summary>
    public string RecipientPersona { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
