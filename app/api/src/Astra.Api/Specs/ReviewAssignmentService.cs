using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Specs;

/// <summary>
/// Assigning a spec's review to one person. The people are the Users directory (Roles &amp; Permissions), which exists
/// whichever way people sign in; only an SME or Admin can be assigned, and only an Engineer or Admin assigns. Each
/// change is audited. Shared by the REST route and the conversation tool so both say and do the same thing.
/// </summary>
public sealed class ReviewAssignmentService(AppDbContext db, IAuditLogger audit)
{
    public sealed record Assignee(Guid Id, string DisplayName);
    public sealed record Failure(int Status, string Code, string Message);

    public static bool MayAssign(Persona p) => p is Persona.Engineer or Persona.Admin;

    /// <summary>SMEs and Admins in the directory, by name.</summary>
    public Task<List<Assignee>> ReviewersAsync(CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.Persona == "sme" || u.Persona == "admin")
            .OrderBy(u => u.DisplayName)
            .Select(u => new Assignee(u.Id, u.DisplayName))
            .ToListAsync(ct);

    /// <summary>A reviewer by id, or by name / e-mail (exact, then unique prefix) for the conversation.</summary>
    public async Task<(Assignee? Who, Failure? Error)> FindReviewerAsync(string idOrName, CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking()
            .Where(u => u.Persona == "sme" || u.Persona == "admin").ToListAsync(ct);
        var q = idOrName.Trim().TrimStart('@');
        User? hit = Guid.TryParse(q, out var gid) ? people.FirstOrDefault(u => u.Id == gid) : null;
        hit ??= people.FirstOrDefault(u => string.Equals(u.DisplayName, q, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(u.Email, q, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            var prefix = people.Where(u => u.DisplayName.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefix.Count == 1) hit = prefix[0];
        }
        if (hit is null)
            return (null, new Failure(400, "reviewer.invalid",
                people.Count == 0
                    ? "No SME or Admin is in the Users list yet — an Admin adds people under Platform → Roles & Permissions."
                    : $"No SME or Admin called “{q}”. Reviewers: {string.Join(", ", people.Select(p => p.DisplayName))}."));
        return (new Assignee(hit.Id, hit.DisplayName), null);
    }

    /// <summary>Assign (or, with <paramref name="userId"/> null, clear) the review of a spec.</summary>
    public async Task<(Assignee? Who, Failure? Error)> AssignAsync(
        Guid specId, Guid? userId, DevPersonaContext actor, HttpContext? http, CancellationToken ct)
    {
        if (!MayAssign(actor.Persona))
            return (null, new Failure(403, "persona.forbidden", "An Engineer or Admin assigns reviews."));
        var spec = await db.Specs.FirstOrDefaultAsync(s => s.Id == specId, ct);
        if (spec is null) return (null, new Failure(404, "spec.not_found", "No such spec."));
        Assignee? who = null;
        if (userId is { } uid)
        {
            var (found, error) = await FindReviewerAsync(uid.ToString(), ct);
            if (error is not null) return (null, error);
            who = found;
        }
        spec.AssignedUserId = who?.Id;
        spec.AssignedAt = who is null ? null : DateTimeOffset.UtcNow;
        spec.AssignedBy = who is null ? null : actor.DisplayName;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(who is null ? "spec.unassigned" : "spec.assigned", "spec", spec.Id, actor,
            new { userId = who?.Id, displayName = who?.DisplayName }, http, ct);
        return (who, null);
    }
}
