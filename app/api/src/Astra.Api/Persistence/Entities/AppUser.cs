namespace Astra.Api.Persistence.Entities;

/// <summary>
/// A person who signs in to the platform (password mode). Only a salted PBKDF2 hash of the password is kept; the
/// personas list says which roles the user may act as (the persona switcher stays within it).
/// </summary>
public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Comma-separated personas the user may act as, e.g. "admin,engineer,sme,observer".</summary>
    public string Personas { get; set; } = "observer";
    public string PasswordHash { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A signed-in session. Only the SHA-256 of the bearer token is stored.</summary>
public sealed class AppSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
