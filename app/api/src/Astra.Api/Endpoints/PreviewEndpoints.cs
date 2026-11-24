using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Preview;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Run a generated React + Python package as a live app in its own short-lived container.
///   POST   /api/v1/scaffolds/{id}/preview   start (or return the one already running)
///   GET    /api/v1/scaffolds/{id}/preview   status: STARTING → READY (url), FAILED, or 404 when none
///   DELETE /api/v1/scaffolds/{id}/preview   stop it now
/// </summary>
public static class PreviewEndpoints
{
    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/previews/status", (PreviewService svc) => Results.Ok(new { enabled = svc.Enabled }));

        app.MapPost("/api/v1/scaffolds/{id:guid}/preview", async (
            Guid id, AppDbContext db, PreviewService svc, DevPersonaContext persona, CancellationToken ct) =>
        {
            if (persona.Persona is Persona.Observer)
                return Error(StatusCodes.Status403Forbidden, "auth.writer_required", "Starting a preview needs an Engineer, SME or Admin.");
            if (!svc.Enabled)
                return Error(StatusCodes.Status409Conflict, "preview.disabled", "Previews are not switched on for this environment.");

            var scaffold = await db.Scaffolds.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (scaffold is null) return Error(StatusCodes.Status404NotFound, "scaffold.not_found", "No such scaffold.");
            if (!string.Equals(scaffold.TargetPlatform, "react-python", StringComparison.OrdinalIgnoreCase))
                return Error(StatusCodes.Status400BadRequest, "preview.unsupported_target",
                    $"Only React + Python packages can be previewed; this one is {scaffold.TargetPlatform}.");
            try
            {
                return Results.Accepted($"/api/v1/scaffolds/{id}/preview", await svc.StartAsync(id, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status409Conflict, "preview.start_failed", ex.Message);
            }
        });

        app.MapGet("/api/v1/scaffolds/{id:guid}/preview", async (Guid id, PreviewService svc, CancellationToken ct) =>
        {
            if (!svc.Enabled) return Error(StatusCodes.Status409Conflict, "preview.disabled", "Previews are not switched on for this environment.");
            try
            {
                var info = await svc.GetAsync(id, ct);
                return info is null ? Error(StatusCodes.Status404NotFound, "preview.none", "No preview is running for this scaffold.") : Results.Ok(info);
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status502BadGateway, "preview.status_failed", ex.Message);
            }
        });

        app.MapDelete("/api/v1/scaffolds/{id:guid}/preview", async (
            Guid id, PreviewService svc, DevPersonaContext persona, CancellationToken ct) =>
        {
            if (persona.Persona is Persona.Observer)
                return Error(StatusCodes.Status403Forbidden, "auth.writer_required", "Stopping a preview needs an Engineer, SME or Admin.");
            if (!svc.Enabled) return Error(StatusCodes.Status409Conflict, "preview.disabled", "Previews are not switched on for this environment.");
            try
            {
                return await svc.StopAsync(id, ct) ? Results.Ok(new { stopped = true }) : Results.Ok(new { stopped = false });
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status502BadGateway, "preview.stop_failed", ex.Message);
            }
        });

        return app;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);
}
