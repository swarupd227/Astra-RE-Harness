using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        // Per-spec audit trail — chronological ascending so the UI can render
        // "oldest at the bottom" by reversing locally.
        app.MapGet("/api/v1/specs/{id:guid}/audit", async (
            Guid id,
            AppDbContext db,
            string? type,
            string? actor,
            int? limit,
            CancellationToken ct) =>
        {
            var q = db.AuditEvents.AsNoTracking().AsQueryable();

            // Match events where the spec is either the direct target,
            // or the subroutine target whose spec_id matches, or a scaffold
            // derived from this spec (so validation.* events surface here).
            var spec = await db.Specs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (spec is null) return Results.NotFound(new { error = new { code = "spec.not_found" } });

            var derivedScaffoldIds = await db.Scaffolds.AsNoTracking()
                .Where(s => s.SpecId == spec.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);

            q = q.Where(e =>
                (e.TargetType == "spec" && e.TargetId == spec.Id) ||
                (e.TargetType == "subroutine" && e.TargetId == spec.SubroutineId) ||
                (e.TargetType == "scaffold" && e.TargetId != null && derivedScaffoldIds.Contains(e.TargetId.Value)));

            if (!string.IsNullOrEmpty(type)) q = q.Where(e => e.EventType == type);
            if (!string.IsNullOrEmpty(actor)) q = q.Where(e => e.ActorPersona == actor);

            var rows = await q
                .OrderByDescending(e => e.OccurredAt)
                .Take(limit ?? 200)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                data = rows.Select(e => new
                {
                    id = e.Id,
                    eventType = e.EventType,
                    actorPersona = e.ActorPersona,
                    actorDisplay = e.ActorDisplay,
                    targetType = e.TargetType,
                    targetId = e.TargetId,
                    occurredAt = e.OccurredAt,
                    payload = e.Payload.RootElement.Clone(),
                }),
            });
        });

        // Global / cross-spec timeline
        app.MapGet("/api/v1/audit", async (
            AppDbContext db,
            string? type,
            string? actor,
            string? targetType,
            int? limit,
            CancellationToken ct) =>
        {
            var q = db.AuditEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(type)) q = q.Where(e => e.EventType == type);
            if (!string.IsNullOrEmpty(actor)) q = q.Where(e => e.ActorPersona == actor);
            if (!string.IsNullOrEmpty(targetType)) q = q.Where(e => e.TargetType == targetType);

            var rows = await q.OrderByDescending(e => e.OccurredAt).Take(limit ?? 200).ToListAsync(ct);

            return Results.Ok(new
            {
                data = rows.Select(e => new
                {
                    id = e.Id,
                    eventType = e.EventType,
                    actorPersona = e.ActorPersona,
                    actorDisplay = e.ActorDisplay,
                    targetType = e.TargetType,
                    targetId = e.TargetId,
                    occurredAt = e.OccurredAt,
                    payload = e.Payload.RootElement.Clone(),
                }),
            });
        });

        return app;
    }
}
