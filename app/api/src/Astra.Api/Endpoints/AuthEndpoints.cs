using Astra.Api.Audit;
using Astra.Api.Auth;
using Microsoft.Extensions.Options;

namespace Astra.Api.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record CreateUserRequest(string? Username, string? DisplayName, string? Personas, string? Password);

/// <summary>
/// Sign-in (Auth:Mode=password). <c>config</c> tells the frontend whether a sign-in is needed; <c>login</c> trades a
/// username and password for a bearer token; <c>me</c> and <c>logout</c> act on the current session; an admin adds
/// users. In dev-header mode the routes answer but nobody needs them.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth/config", (IOptions<DevPersonaOptions> opts) =>
            Results.Ok(new { mode = opts.Value.PasswordMode ? AuthModes.Password : AuthModes.DevHeader }));

        app.MapPost("/api/v1/auth/login", async (LoginRequest body, AuthService auth, IAuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            var r = await auth.LoginAsync(body.Username, body.Password, ct);
            if (r.Token is null)
            {
                await audit.LogAsync("auth.sign_in_failed", "user", null, null, new { username = (body.Username ?? "").Trim().ToLowerInvariant(), reason = r.ErrorCode }, http, ct);
                return Results.Json(new { error = new { code = r.ErrorCode, message = r.Error } },
                    statusCode: r.ErrorCode == "auth.locked" ? StatusCodes.Status423Locked : StatusCodes.Status401Unauthorized);
            }
            await audit.LogAsync("auth.signed_in", "user", r.User!.Id, null, new { username = r.User.Username }, http, ct);
            return Results.Ok(new { token = r.Token, expiresAt = r.ExpiresAt, user = Shape(r.User) });
        });

        app.MapPost("/api/v1/auth/logout", async (HttpContext http, AuthService auth, CancellationToken ct) =>
        {
            await auth.LogoutAsync(DevPersonaMiddleware.TokenOf(http.Request), ct);
            return Results.Ok(new { signedOut = true });
        });

        app.MapGet("/api/v1/auth/me", (HttpContext http, DevPersonaContext persona) =>
            http.Items["user"] is SignedInUser u
                ? Results.Ok(new { user = Shape(u), persona = persona.Persona.ToString().ToLowerInvariant() })
                : Results.Ok(new { user = (object?)null, persona = persona.Persona.ToString().ToLowerInvariant() }));

        app.MapPost("/api/v1/auth/users", async (CreateUserRequest body, AuthService auth, DevPersonaContext actor, IAuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.Json(new { error = new { code = "auth.admin_required", message = "Only an Admin adds users." } }, statusCode: 403);
            var (user, error) = await auth.CreateUserAsync(body.Username, body.DisplayName, body.Personas, body.Password, ct);
            if (user is null) return Results.BadRequest(new { error = new { code = "auth.user_invalid", message = error } });
            await audit.LogAsync("auth.user_created", "user", user.Id, actor, new { username = user.Username, personas = user.Personas }, http, ct);
            return Results.Ok(new { user = Shape(user) });
        });

        return app;
    }

    private static object Shape(SignedInUser u) => new
    {
        id = u.Id,
        username = u.Username,
        displayName = u.DisplayName,
        personas = u.Personas.Select(p => p.ToString().ToLowerInvariant()).ToArray(),
    };
}
