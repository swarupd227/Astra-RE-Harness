using Microsoft.Extensions.Options;

namespace Astra.Api.Auth;

/// <summary>
///     Phase A: reads the X-Dev-Persona header to set the per-request persona.
///     If the bypass is OFF or the header is absent, falls back to the configured default.
///     IMPORTANT: this middleware MUST be removed (or made inert) once OIDC ships in Phase C.
/// </summary>
public sealed class DevPersonaMiddleware
{
    public const string HeaderName = "X-Dev-Persona";

    private readonly RequestDelegate _next;
    private readonly DevPersonaOptions _options;
    private readonly ILogger<DevPersonaMiddleware> _logger;

    public DevPersonaMiddleware(
        RequestDelegate next,
        IOptions<DevPersonaOptions> options,
        ILogger<DevPersonaMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Paths that answer without a sign-in in password mode: health probes, the sign-in itself, and the
    /// sidecars' internal callbacks (which carry their own shared secret).</summary>
    public static bool IsOpenPath(PathString path) =>
        path == "/" || path.StartsWithSegments("/health") || path.StartsWithSegments("/internal")
        || path.StartsWithSegments("/api/v1/auth/login") || path.StartsWithSegments("/api/v1/auth/config");

    /// <summary>The bearer token of a request: the Authorization header, or (for links and event streams, which
    /// cannot send headers) an <c>access_token</c> query parameter on a GET.</summary>
    public static string? TokenOf(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        return HttpMethods.IsGet(req.Method) ? req.Query["access_token"].ToString() is { Length: > 0 } q ? q : null : null;
    }

    public async Task InvokeAsync(HttpContext ctx, DevPersonaContext personaCtx)
    {
        if (_options.PasswordMode)
        {
            await PasswordModeAsync(ctx, personaCtx);
            return;
        }

        if (!_options.DevPersonaBypass)
        {
            // Real auth would run here. Phase A: log and 501.
            ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "auth.not_configured",
                    message = "Real authentication not wired yet. Set Auth__DevPersonaBypass=true for local dev."
                }
            });
            return;
        }

        var headerValue = ctx.Request.Headers.TryGetValue(HeaderName, out var raw)
            ? raw.ToString()
            : null;

        var fallback = _options.DevPersonaDefault.ParsePersona(Persona.Engineer);
        personaCtx.Persona = headerValue.ParsePersona(fallback);
        personaCtx.DisplayName = headerValue is null
            ? "Dev User (default persona)"
            : $"Dev User ({personaCtx.Persona.DisplayName()})";
        personaCtx.IsBypass = true;

        ctx.Items["persona"] = personaCtx.Persona;
        await _next(ctx);
    }

    // Password mode: no session, no API. The X-Dev-Persona header only picks one of the personas the user holds.
    private async Task PasswordModeAsync(HttpContext ctx, DevPersonaContext personaCtx)
    {
        if (HttpMethods.IsOptions(ctx.Request.Method) || IsOpenPath(ctx.Request.Path))
        {
            personaCtx.Persona = Persona.Observer;
            personaCtx.DisplayName = "Anonymous";
            personaCtx.IsBypass = false;
            await _next(ctx);
            return;
        }

        var auth = ctx.RequestServices.GetRequiredService<AuthService>();
        var user = await auth.ValidateAsync(TokenOf(ctx.Request), ctx.RequestAborted);
        if (user is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "auth.required", message = "Sign in to use the platform." } });
            return;
        }

        var requested = ctx.Request.Headers.TryGetValue(HeaderName, out var raw) ? raw.ToString() : null;
        personaCtx.Persona = AuthService.Resolve(user.Personas, requested);
        personaCtx.DisplayName = $"{user.DisplayName} ({personaCtx.Persona.DisplayName()})";
        personaCtx.IsBypass = false;
        ctx.Items["persona"] = personaCtx.Persona;
        ctx.Items["user"] = user;
        await _next(ctx);
    }
}
