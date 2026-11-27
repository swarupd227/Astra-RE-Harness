using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Auth;

public sealed record SignedInUser(Guid Id, string Username, string DisplayName, IReadOnlyList<Persona> Personas);

public sealed record LoginOutcome(string? Token, DateTimeOffset? ExpiresAt, SignedInUser? User, string? ErrorCode, string? Error);

/// <summary>
/// Password sign-in: users, sessions, lockout. A wrong password and an unknown user get the same answer; five wrong
/// passwords in a row lock the account for 15 minutes.
/// </summary>
public sealed class AuthService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly DevPersonaOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(AppDbContext db, IOptions<DevPersonaOptions> options, ILogger<AuthService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public static IReadOnlyList<Persona> ParsePersonas(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ParsePersona((Persona)(-1)))
            .Where(p => Enum.IsDefined(p))
            .Distinct()
            .ToList();

    /// <summary>The persona a request acts as: the one it asks for when the user holds it, else the user's first.</summary>
    public static Persona Resolve(IReadOnlyList<Persona> held, string? requested)
    {
        if (held.Count == 0) return Persona.Observer;
        var asked = requested.ParsePersona((Persona)(-1));
        return held.Contains(asked) ? asked : held[0];
    }

    public async Task<LoginOutcome> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        var name = (username ?? "").Trim().ToLowerInvariant();
        var user = name.Length == 0 ? null : await _db.AppUsers.FirstOrDefaultAsync(u => u.Username == name, ct);
        var now = DateTimeOffset.UtcNow;
        if (user is null)
        {
            // Same work and the same answer as a wrong password, so a name cannot be probed.
            PasswordHasher.Verify(password ?? "", PasswordHasher.Hash("not-a-user"));
            return new(null, null, null, "auth.invalid", "Wrong username or password.");
        }
        if (user.LockedUntil is { } until && until > now)
            return new(null, null, null, "auth.locked", "Too many wrong passwords. Try again in a few minutes.");

        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= MaxFailedAttempts)
            {
                user.LockedUntil = now.Add(LockoutFor);
                user.FailedAttempts = 0;
                _logger.LogWarning("Sign-in locked for {User} after {Count} wrong passwords", user.Username, MaxFailedAttempts);
            }
            await _db.SaveChangesAsync(ct);
            return new(null, null, null, "auth.invalid", "Wrong username or password.");
        }

        user.FailedAttempts = 0;
        user.LockedUntil = null;
        var token = SessionTokens.New();
        var expires = now.AddHours(Math.Clamp(_options.SessionHours, 1, 24 * 7));
        _db.AppSessions.Add(new AppSession { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = SessionTokens.Hash(token), CreatedAt = now, ExpiresAt = expires });
        // Expired sessions are dropped as new ones are made.
        await _db.AppSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Signed in: {User}", user.Username);
        return new(token, expires, ToSignedIn(user), null, null);
    }

    public async Task<SignedInUser?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = SessionTokens.Hash(token);
        var now = DateTimeOffset.UtcNow;
        var user = await (from s in _db.AppSessions.AsNoTracking()
                          join u in _db.AppUsers.AsNoTracking() on s.UserId equals u.Id
                          where s.TokenHash == hash && s.ExpiresAt > now
                          select u).FirstOrDefaultAsync(ct);
        return user is null ? null : ToSignedIn(user);
    }

    public async Task LogoutAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var hash = SessionTokens.Hash(token);
        await _db.AppSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
    }

    /// <summary>Adds a user (an admin's action). Refuses a short password or a name already taken.</summary>
    public async Task<(SignedInUser? User, string? Error)> CreateUserAsync(string? username, string? displayName, string? personas, string? password, CancellationToken ct)
    {
        var name = (username ?? "").Trim().ToLowerInvariant();
        if (name.Length is < 2 or > 64 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@')))
            return (null, "A username is 2-64 letters, digits, '.', '-', '_' or '@'.");
        if ((password ?? "").Length < PasswordHasher.MinLength)
            return (null, $"A password has at least {PasswordHasher.MinLength} characters.");
        var held = ParsePersonas(personas);
        if (held.Count == 0) return (null, "Give the user at least one persona (admin, engineer, sme, observer).");
        if (await _db.AppUsers.AnyAsync(u => u.Username == name, ct)) return (null, "That username is taken.");
        var user = new AppUser
        {
            Id = Guid.NewGuid(), Username = name, DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
            Personas = string.Join(',', held.Select(p => p.ToString().ToLowerInvariant())),
            PasswordHash = PasswordHasher.Hash(password!), CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync(ct);
        return (ToSignedIn(user), null);
    }

    /// <summary>At start-up in password mode: create the configured first admin when there is no user at all.</summary>
    public async Task EnsureBootstrapAdminAsync(CancellationToken ct)
    {
        if (!_options.PasswordMode) return;
        if (await _db.AppUsers.AnyAsync(ct)) return;
        if (string.IsNullOrWhiteSpace(_options.BootstrapAdminUser) || string.IsNullOrEmpty(_options.BootstrapAdminPassword))
        {
            _logger.LogWarning("Auth:Mode=password but no user exists and Auth:BootstrapAdminUser/Password are not set: nobody can sign in yet.");
            return;
        }
        var (user, error) = await CreateUserAsync(_options.BootstrapAdminUser, "Administrator", "admin,engineer,sme,observer", _options.BootstrapAdminPassword, ct);
        if (user is null) _logger.LogError("The first admin could not be created: {Error}", error);
        else _logger.LogInformation("First admin created: {User}", user.Username);
    }

    private static SignedInUser ToSignedIn(AppUser u) => new(u.Id, u.Username, u.DisplayName, ParsePersonas(u.Personas));
}
