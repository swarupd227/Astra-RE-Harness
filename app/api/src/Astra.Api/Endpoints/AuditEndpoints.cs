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
            DateTimeOffset? since,
            DateTimeOffset? until,
            int? limit,
            int? offset,
            CancellationToken ct) =>
        {
            var q = db.AuditEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(type)) q = q.Where(e => e.EventType == type);
            if (!string.IsNullOrEmpty(actor)) q = q.Where(e => e.ActorPersona == actor);
            if (!string.IsNullOrEmpty(targetType)) q = q.Where(e => e.TargetType == targetType);
            if (since is { } s) q = q.Where(e => e.OccurredAt >= s);
            if (until is { } u) q = q.Where(e => e.OccurredAt <= u);

            var rows = await q.OrderByDescending(e => e.OccurredAt)
                .Skip(Math.Max(0, offset ?? 0))
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .ToListAsync(ct);

            // Names are a convenience: if looking them up fails, the feed is still served, just without them.
            Dictionary<Guid, TargetName> names;
            try { names = await ResolveTargetsAsync(db, rows, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Warning(ex, "Audit feed: naming targets failed; serving ids only");
                names = new();
            }
            return Results.Ok(new
            {
                data = rows.Select(e =>
                {
                    var t = e.TargetId is { } id && names.TryGetValue(id, out var n) ? n : null;
                    return new
                    {
                        id = e.Id,
                        eventType = e.EventType,
                        actorPersona = e.ActorPersona,
                        actorDisplay = e.ActorDisplay,
                        targetType = e.TargetType,
                        targetId = e.TargetId,
                        // What the event was about, in words ("ProcessClaim", a project name) and where to open it;
                        // null when the target no longer exists or is of a kind not named here.
                        targetName = t?.Name,
                        subroutineId = t?.SubroutineId,
                        occurredAt = e.OccurredAt,
                        payload = e.Payload.RootElement.Clone(),
                    };
                }),
            });
        });

        return app;
    }

    private sealed record TargetName(string Name, Guid? SubroutineId);

    /// <summary>
    /// Names the targets of a page of audit rows in four set queries: a spec or a generated package is named by its
    /// code unit, a project by its name. Ids of other kinds, or of rows since removed, are simply absent.
    /// </summary>
    private static async Task<Dictionary<Guid, TargetName>> ResolveTargetsAsync(
        AppDbContext db, IReadOnlyList<Persistence.Entities.AuditEvent> rows, CancellationToken ct)
    {
        Guid[] Ids(string type) => rows.Where(r => r.TargetType == type && r.TargetId is not null)
            .Select(r => r.TargetId!.Value).Distinct().ToArray();
        var specIds = Ids("spec");
        var scaffoldIds = Ids("scaffold");
        var corpusIds = Ids("corpus");
        var directSubIds = Ids("subroutine");

        var scaffoldSpec = scaffoldIds.Length == 0 ? new Dictionary<Guid, Guid>()
            : await db.Scaffolds.AsNoTracking().Where(s => scaffoldIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.SpecId, ct);
        var allSpecIds = specIds.Concat(scaffoldSpec.Values).Distinct().ToArray();
        var specSub = allSpecIds.Length == 0 ? new Dictionary<Guid, Guid>()
            : await db.Specs.AsNoTracking().Where(s => allSpecIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.SubroutineId, ct);
        var subIds = specSub.Values.Concat(directSubIds).Distinct().ToArray();
        var subName = subIds.Length == 0 ? new Dictionary<Guid, string>()
            : await db.Subroutines.AsNoTracking().Where(s => subIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var corpusName = corpusIds.Length == 0 ? new Dictionary<Guid, string>()
            : await db.Corpora.AsNoTracking().Where(c => corpusIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var result = new Dictionary<Guid, TargetName>();
        void FromSpec(Guid target, Guid specId)
        {
            if (specSub.TryGetValue(specId, out var sub) && subName.TryGetValue(sub, out var name))
                result[target] = new TargetName(name, sub);
        }
        foreach (var id in specIds) FromSpec(id, id);
        foreach (var (id, spec) in scaffoldSpec) FromSpec(id, spec);
        foreach (var id in directSubIds)
            if (subName.TryGetValue(id, out var name)) result[id] = new TargetName(name, id);
        foreach (var (id, name) in corpusName) result[id] = new TargetName(name, null);
        return result;
    }
}
