using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase C.7: threaded comments + @-mentions.
///
/// Comments scope to either:
///   - an entire spec (claimPath = null), or
///   - a single claim via its JSONPath (claimPath = "$.invariants[?(@.id=='INV-1')]")
///
/// Replies use parentCommentId. The API returns the flat list ordered
/// by created_at; the frontend builds the tree.
///
/// Mentions: <c>@engineer</c>, <c>@sme</c>, <c>@observer</c>, <c>@admin</c>
/// in the body trigger a Notification row per persona.
/// </summary>
public static class CommentEndpoints
{
    private const int MaxBodyChars = 8_000;

    // Map persona name → display label for notification payloads.
    private static readonly HashSet<string> ValidPersonas =
        new(StringComparer.OrdinalIgnoreCase) { "engineer", "sme", "observer", "admin" };

    // Match @engineer, @sme, @admin, @observer (word-boundary). Persona normalised to lowercase.
    private static readonly Regex MentionRx =
        new(@"(?<![A-Za-z0-9_])@(engineer|sme|observer|admin)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder app)
    {
        // ── List comments for a spec ─────────────────────────────────
        app.MapGet("/api/v1/specs/{specId:guid}/comments", async (
            Guid specId,
            string? claimPath,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!await db.Specs.AsNoTracking().AnyAsync(s => s.Id == specId, ct))
                return Results.NotFound(new { error = new { code = "spec.not_found" } });

            var query = db.Comments.AsNoTracking().Where(c => c.SpecId == specId);
            if (!string.IsNullOrWhiteSpace(claimPath))
                query = query.Where(c => c.ClaimPath == claimPath);

            var rows = await query
                .OrderBy(c => c.CreatedAt)
                .Select(c => Project(c))
                .ToListAsync(ct);

            return Results.Ok(new { data = rows });
        });

        // ── Post a comment (top-level or reply) ──────────────────────
        app.MapPost("/api/v1/specs/{specId:guid}/comments", async (
            Guid specId,
            PostCommentRequest body,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Body))
                return Bad("comment.body_required", "Comment body is required.");
            if (body.Body.Length > MaxBodyChars)
                return Bad("comment.body_too_long", $"Body must be ≤ {MaxBodyChars} chars.");

            if (!await db.Specs.AsNoTracking().AnyAsync(s => s.Id == specId, ct))
                return Results.NotFound(new { error = new { code = "spec.not_found" } });

            if (body.ParentCommentId is { } parentId)
            {
                var parentSpecId = await db.Comments
                    .AsNoTracking()
                    .Where(c => c.Id == parentId)
                    .Select(c => (Guid?)c.SpecId)
                    .FirstOrDefaultAsync(ct);
                if (parentSpecId is null)
                    return Bad("comment.parent_not_found", "Parent comment does not exist.");
                if (parentSpecId != specId)
                    return Bad("comment.parent_wrong_spec", "Parent belongs to a different spec.");
            }

            var mentions = DetectMentions(body.Body);

            var now = DateTimeOffset.UtcNow;
            var comment = new Comment
            {
                Id = Guid.NewGuid(),
                SpecId = specId,
                ClaimPath = string.IsNullOrWhiteSpace(body.ClaimPath) ? null : body.ClaimPath,
                ParentCommentId = body.ParentCommentId,
                Body = body.Body,
                MentionedPersonas = JsonDocument.Parse(JsonSerializer.Serialize(mentions)),
                AuthorId = null,
                AuthorPersona = persona.Persona.ToString().ToLowerInvariant(),
                AuthorDisplay = persona.DisplayName,
                CreatedAt = now,
            };
            db.Comments.Add(comment);

            // Fan-out notifications — one per mentioned persona, excluding the author.
            foreach (var m in mentions)
            {
                if (string.Equals(m, comment.AuthorPersona, StringComparison.OrdinalIgnoreCase)) continue;
                var payload = new
                {
                    specId,
                    claimPath = comment.ClaimPath,
                    commentId = comment.Id,
                    parentCommentId = comment.ParentCommentId,
                    excerpt = Excerpt(comment.Body, 240),
                    authorPersona = comment.AuthorPersona,
                    authorDisplay = comment.AuthorDisplay,
                };
                db.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    RecipientPersona = m,
                    Type = "comment.mention",
                    TargetType = "comment",
                    TargetId = comment.Id,
                    Payload = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
                    CreatedAt = now,
                    ActorPersona = comment.AuthorPersona,
                    ActorDisplay = comment.AuthorDisplay,
                });
            }

            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "comment.posted",
                comment.ClaimPath is null ? "spec" : "claim",
                comment.ClaimPath is null ? specId : (Guid?)null,
                actor: persona,
                payload: new
                {
                    commentId = comment.Id,
                    specId,
                    claimPath = comment.ClaimPath,
                    parentCommentId = comment.ParentCommentId,
                    mentions,
                    bodyChars = comment.Body.Length,
                },
                ct: ct);

            // Reload to project consistently.
            var saved = await db.Comments.AsNoTracking().FirstAsync(c => c.Id == comment.Id, ct);
            return Results.Created($"/api/v1/comments/{saved.Id}", Project(saved));
        });

        // ── Edit comment body (author only) ──────────────────────────
        app.MapPatch("/api/v1/comments/{id:guid}", async (
            Guid id,
            EditCommentRequest body,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Body))
                return Bad("comment.body_required", "Body is required.");
            if (body.Body.Length > MaxBodyChars)
                return Bad("comment.body_too_long", $"Body must be ≤ {MaxBodyChars} chars.");

            var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (comment is null) return Results.NotFound(new { error = new { code = "comment.not_found" } });
            if (comment.DeletedAt is not null)
                return Bad("comment.deleted", "Cannot edit a deleted comment.");

            var authorPersona = persona.Persona.ToString().ToLowerInvariant();
            if (!string.Equals(comment.AuthorPersona, authorPersona, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = new { code = "comment.forbidden", message = "Only the author can edit." } }, statusCode: 403);

            var oldMentions = comment.MentionedPersonas.RootElement.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newMentions = DetectMentions(body.Body);
            var newMentionsSet = new HashSet<string>(newMentions, StringComparer.OrdinalIgnoreCase);
            var addedMentions = newMentionsSet.Except(oldMentions, StringComparer.OrdinalIgnoreCase).ToList();

            comment.Body = body.Body;
            comment.EditedAt = DateTimeOffset.UtcNow;
            comment.MentionedPersonas = JsonDocument.Parse(JsonSerializer.Serialize(newMentions));

            // Newly-added mentions get a fresh notification.
            foreach (var m in addedMentions)
            {
                if (string.Equals(m, comment.AuthorPersona, StringComparison.OrdinalIgnoreCase)) continue;
                db.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    RecipientPersona = m,
                    Type = "comment.mention",
                    TargetType = "comment",
                    TargetId = comment.Id,
                    Payload = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        specId = comment.SpecId,
                        claimPath = comment.ClaimPath,
                        commentId = comment.Id,
                        excerpt = Excerpt(comment.Body, 240),
                        authorPersona = comment.AuthorPersona,
                        authorDisplay = comment.AuthorDisplay,
                        reason = "edited",
                    })),
                    CreatedAt = DateTimeOffset.UtcNow,
                    ActorPersona = comment.AuthorPersona,
                    ActorDisplay = comment.AuthorDisplay,
                });
            }

            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "comment.edited",
                comment.ClaimPath is null ? "spec" : "claim",
                comment.ClaimPath is null ? comment.SpecId : (Guid?)null,
                actor: persona,
                payload: new
                {
                    commentId = comment.Id,
                    specId = comment.SpecId,
                    claimPath = comment.ClaimPath,
                    addedMentions,
                    bodyChars = comment.Body.Length,
                },
                ct: ct);

            var saved = await db.Comments.AsNoTracking().FirstAsync(c => c.Id == comment.Id, ct);
            return Results.Ok(Project(saved));
        });

        // ── Resolve / unresolve ──────────────────────────────────────
        app.MapPost("/api/v1/comments/{id:guid}/resolve", async (
            Guid id,
            ResolveCommentRequest? body,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (comment is null) return Results.NotFound(new { error = new { code = "comment.not_found" } });

            var unresolve = body?.Unresolve == true;
            if (unresolve)
            {
                comment.ResolvedAt = null;
                comment.ResolvedByPersona = null;
            }
            else
            {
                comment.ResolvedAt = DateTimeOffset.UtcNow;
                comment.ResolvedByPersona = persona.Persona.ToString().ToLowerInvariant();
            }
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                unresolve ? "comment.unresolved" : "comment.resolved",
                comment.ClaimPath is null ? "spec" : "claim",
                comment.ClaimPath is null ? comment.SpecId : (Guid?)null,
                actor: persona,
                payload: new { commentId = comment.Id, specId = comment.SpecId, claimPath = comment.ClaimPath },
                ct: ct);

            var saved = await db.Comments.AsNoTracking().FirstAsync(c => c.Id == comment.Id, ct);
            return Results.Ok(Project(saved));
        });

        // ── Soft-delete (author only) ────────────────────────────────
        app.MapDelete("/api/v1/comments/{id:guid}", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (comment is null) return Results.NotFound(new { error = new { code = "comment.not_found" } });

            var authorPersona = persona.Persona.ToString().ToLowerInvariant();
            if (!string.Equals(comment.AuthorPersona, authorPersona, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = new { code = "comment.forbidden", message = "Only the author can delete." } }, statusCode: 403);
            if (comment.DeletedAt is not null) return Results.NoContent();

            comment.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "comment.deleted",
                comment.ClaimPath is null ? "spec" : "claim",
                comment.ClaimPath is null ? comment.SpecId : (Guid?)null,
                actor: persona,
                payload: new { commentId = comment.Id, specId = comment.SpecId, claimPath = comment.ClaimPath },
                ct: ct);

            return Results.NoContent();
        });

        return app;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    public static IReadOnlyList<string> DetectMentions(string body)
    {
        var matches = MentionRx.Matches(body);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (Match m in matches)
        {
            var p = m.Groups[1].Value.ToLowerInvariant();
            if (ValidPersonas.Contains(p) && seen.Add(p)) ordered.Add(p);
        }
        return ordered;
    }

    private static string Excerpt(string body, int max)
    {
        var s = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static object Project(Comment c) => new
    {
        id = c.Id,
        specId = c.SpecId,
        claimPath = c.ClaimPath,
        parentCommentId = c.ParentCommentId,
        body = c.DeletedAt is null ? c.Body : "_(deleted)_",
        deleted = c.DeletedAt is not null,
        authorPersona = c.AuthorPersona,
        authorDisplay = c.AuthorDisplay,
        mentionedPersonas = c.MentionedPersonas,
        createdAt = c.CreatedAt,
        editedAt = c.EditedAt,
        resolvedAt = c.ResolvedAt,
        resolvedByPersona = c.ResolvedByPersona,
    };

    private static IResult Bad(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });

    public sealed record PostCommentRequest(string Body, string? ClaimPath, Guid? ParentCommentId);
    public sealed record EditCommentRequest(string Body);
    public sealed record ResolveCommentRequest(bool Unresolve);
}
