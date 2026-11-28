using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;

namespace Astra.Api.Auth;

/// <summary>
/// Whose inbox a request reads (the sign-in middleware puts a <see cref="SignedInUser"/> in <c>Items["user"]</c>). Someone signed in with a password is a person ("user:&lt;id&gt;"): their read state and
/// the specs they follow are their own, and they also see their persona's mentions. With the shared dev personas the
/// key is the persona ("persona:sme") and everything behaves as it always did — one inbox per persona, read state on
/// the notification row itself.
/// </summary>
public sealed record InboxScope(string Persona, string Key, bool Personal)
{
    public static InboxScope For(HttpContext http, DevPersonaContext persona)
    {
        var p = persona.Persona.ToString().ToLowerInvariant();
        return http.Items["user"] is SignedInUser u
            ? new InboxScope(p, $"user:{u.Id}", true)
            : new InboxScope(p, $"persona:{p}", false);
    }

    /// <summary>The notifications addressed to this inbox: the persona's, plus (for a person) their own.</summary>
    public IQueryable<Notification> Mine(AppDbContext db)
    {
        var (p, key) = (Persona, Key);
        return Personal
            ? db.Notifications.Where(n => (n.RecipientPersona == p && n.RecipientUserKey == null) || n.RecipientUserKey == key)
            : db.Notifications.Where(n => n.RecipientPersona == p && n.RecipientUserKey == null);
    }

    public IQueryable<Notification> Unread(AppDbContext db)
    {
        var key = Key;
        // A person also honours a read mark already on the row (set while the platform ran with shared personas), so
        // switching to password sign-in does not bring the whole history back as unread.
        return Personal
            ? Mine(db).Where(n => n.ReadAt == null && !db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserKey == key))
            : Mine(db).Where(n => n.ReadAt == null);
    }
}
