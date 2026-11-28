using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
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

        // ── List: this inbox (a persona's, or a signed-in person's — see InboxScope) ──
        grp.MapGet("", async (
            bool? unreadOnly,
            int? limit,
            int? offset,
            AppDbContext db,
            DevPersonaContext persona,
            HttpContext http,
            CancellationToken ct) =>
        {
            var inbox = InboxScope.For(http, persona);
            var key = inbox.Key;
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(0, offset ?? 0);

            var q = unreadOnly == true ? inbox.Unread(db) : inbox.Mine(db);
            var totalCount = await q.AsNoTracking().CountAsync(ct);
            var unreadCount = await inbox.Unread(db).CountAsync(ct);

            var rows = await q.AsNoTracking()
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
                    // A person's read mark is theirs; a persona's is on the row.
                    readAt = inbox.Personal
                        ? db.NotificationReads.Where(r => r.NotificationId == n.Id && r.UserKey == key)
                            .Select(r => (DateTimeOffset?)r.ReadAt).FirstOrDefault() ?? n.ReadAt
                        : n.ReadAt,
                    actorPersona = n.ActorPersona,
                    actorDisplay = n.ActorDisplay,
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                data = rows,
                total = totalCount,
                unread = unreadCount,
                persona = inbox.Persona,
                personal = inbox.Personal,
            });
        });

        // ── Cheap polled count for the LeftNav badge ─────────────────
        grp.MapGet("unread-count", async (
            AppDbContext db,
            DevPersonaContext persona,
            HttpContext http,
            CancellationToken ct) =>
        {
            var inbox = InboxScope.For(http, persona);
            var count = await inbox.Unread(db).CountAsync(ct);
            return Results.Ok(new { unread = count, persona = inbox.Persona });
        });

        grp.MapPost("{id:guid}/read", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext persona,
            HttpContext http,
            CancellationToken ct) =>
        {
            var inbox = InboxScope.For(http, persona);
            var n = await inbox.Mine(db).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (n is null)
                return Results.NotFound(new { error = new { code = "notification.not_found" } });
            var now = DateTimeOffset.UtcNow;
            if (!inbox.Personal)
            {
                if (n.ReadAt is null)
                {
                    n.ReadAt = now;
                    await db.SaveChangesAsync(ct);
                }
                return Results.Ok(new { id = n.Id, readAt = n.ReadAt });
            }
            var mark = await db.NotificationReads.FirstOrDefaultAsync(r => r.NotificationId == id && r.UserKey == inbox.Key, ct);
            if (mark is null)
            {
                mark = new NotificationRead { NotificationId = id, UserKey = inbox.Key, ReadAt = now };
                db.NotificationReads.Add(mark);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { id = n.Id, readAt = (DateTimeOffset?)mark.ReadAt });
        });

        grp.MapPost("read-all", async (
            AppDbContext db,
            DevPersonaContext persona,
            HttpContext http,
            CancellationToken ct) =>
        {
            var inbox = InboxScope.For(http, persona);
            var now = DateTimeOffset.UtcNow;
            int marked;
            if (!inbox.Personal)
            {
                marked = await inbox.Unread(db).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
            }
            else
            {
                var ids = await inbox.Unread(db).Select(n => n.Id).ToListAsync(ct);
                db.NotificationReads.AddRange(ids.Select(id => new NotificationRead { NotificationId = id, UserKey = inbox.Key, ReadAt = now }));
                await db.SaveChangesAsync(ct);
                marked = ids.Count;
            }
            return Results.Ok(new { markedRead = marked, persona = inbox.Persona });
        });

        return app;
    }
}
