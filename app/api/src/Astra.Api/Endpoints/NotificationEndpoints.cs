using Astra.Api.Auth;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase C.7: in-app notification inbox. Recipients are identified by
/// persona for now; Phase D will swap to OIDC user ids.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/v1/notifications");

        // ── List: current persona's inbox ────────────────────────────
        grp.MapGet("", async (
            bool? unreadOnly,
            int? limit,
            int? offset,
            AppDbContext db,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            var p = persona.Persona.ToString().ToLowerInvariant();
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(0, offset ?? 0);

            var q = db.Notifications.AsNoTracking().Where(n => n.RecipientPersona == p);
            if (unreadOnly == true) q = q.Where(n => n.ReadAt == null);

            var totalCount = await q.CountAsync(ct);
            var unreadCount = await db.Notifications.CountAsync(
                n => n.RecipientPersona == p && n.ReadAt == null, ct);

            var rows = await q
                .OrderByDescending(n => n.CreatedAt)
                .Skip(skip).Take(take)
                .Select(n => new
                {
                    id = n.Id,
                    recipientPersona = n.RecipientPersona,
                    type = n.Type,
                    targetType = n.TargetType,
                    targetId = n.TargetId,
                    payload = n.Payload,
                    createdAt = n.CreatedAt,
                    readAt = n.ReadAt,
                    actorPersona = n.ActorPersona,
                    actorDisplay = n.ActorDisplay,
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                data = rows,
                total = totalCount,
                unread = unreadCount,
                persona = p,
            });
        });

        // ── Cheap polled count for the LeftNav badge ─────────────────
        grp.MapGet("unread-count", async (
            AppDbContext db,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            var p = persona.Persona.ToString().ToLowerInvariant();
            var count = await db.Notifications
                .CountAsync(n => n.RecipientPersona == p && n.ReadAt == null, ct);
            return Results.Ok(new { unread = count, persona = p });
        });

        grp.MapPost("{id:guid}/read", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            var p = persona.Persona.ToString().ToLowerInvariant();
            var n = await db.Notifications.FirstOrDefaultAsync(
                x => x.Id == id && x.RecipientPersona == p, ct);
            if (n is null)
                return Results.NotFound(new { error = new { code = "notification.not_found" } });
            if (n.ReadAt is null)
            {
                n.ReadAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { id = n.Id, readAt = n.ReadAt });
        });

        grp.MapPost("read-all", async (
            AppDbContext db,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            var p = persona.Persona.ToString().ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var marked = await db.Notifications
                .Where(n => n.RecipientPersona == p && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
            return Results.Ok(new { markedRead = marked, persona = p });
        });

        return app;
    }
}
