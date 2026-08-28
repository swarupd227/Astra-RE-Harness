using System.Text.Json;

namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Phase C.7: threaded comments on specs and claims.
///
/// Scope:
///   - <c>ClaimPath = null</c> → spec-level comment
///   - <c>ClaimPath = "$.invariants[?(@.id=='INV-1')]"</c> → claim-level
///
/// Threading: <c>ParentCommentId</c> points at the comment being replied to.
/// One level of nesting is enough for the demo; UI is free to flatten or
/// nest however it wants since we always return the flat list.
///
/// Mentions: <c>MentionedPersonas</c> is a JSON array of persona strings
/// (engineer | sme | observer | admin). When a comment is posted, a
/// Notification row is created per mentioned persona.
/// </summary>
public sealed class Comment
{
    public Guid Id { get; set; }
    public Guid SpecId { get; set; }

    /// <summary>JSONPath into spec_json (null = spec-level comment).</summary>
    public string? ClaimPath { get; set; }

    /// <summary>Replies anchor on this. Null for top-level comments.</summary>
    public Guid? ParentCommentId { get; set; }

    /// <summary>
    /// Stored raw, exactly as submitted — including any HTML/script-looking text.
    /// This is safe ONLY because every current renderer treats it as plain text
    /// (React text children on the frontend; no dangerouslySetInnerHTML, no
    /// markdown parser touches it). If a consumer ever needs to render this as
    /// HTML or Markdown, it MUST sanitize/encode at that render site — do not
    /// "fix" this by encoding here, which would corrupt legitimate bodies
    /// containing &lt; &gt; &amp; (e.g. "if x &lt; 5") for the existing safe renderer.
    /// </summary>
    public string Body { get; set; } = "";

    /// <summary>JSON array of persona strings — written by the API after parsing the body.</summary>
    public JsonDocument MentionedPersonas { get; set; } = JsonDocument.Parse("[]");

    public Guid? AuthorId { get; set; }
    public string AuthorPersona { get; set; } = "";
    public string AuthorDisplay { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>Set when someone marks the thread/comment resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByPersona { get; set; }

    /// <summary>Soft-delete marker. Body is replaced with "<i>(deleted)</i>" semantics in the API.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public Spec? Spec { get; set; }
}
