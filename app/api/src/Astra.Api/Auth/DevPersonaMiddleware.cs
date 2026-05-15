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

    public async Task InvokeAsync(HttpContext ctx, DevPersonaContext personaCtx)
    {
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
            : $"Dev User ({personaCtx.Persona})";
        personaCtx.IsBypass = true;

        ctx.Items["persona"] = personaCtx.Persona;
        await _next(ctx);
    }
}
