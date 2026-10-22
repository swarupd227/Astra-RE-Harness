using System.Text.Json;
using Astra.Api.Artefacts;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class ProjectSettingsEndpoints
{
    public static IEndpointRouteBuilder MapProjectSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Readable by every persona (the shell reads the banner from here).
        app.MapGet("/api/v1/corpora/{id:guid}/settings", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var json = await db.Corpora.AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => new { c.SettingsJson })
                .FirstOrDefaultAsync(ct);
            if (json is null) return Results.NotFound(new { error = new { code = "corpus.not_found" } });
            return Results.Ok(ProjectSettings.Parse(json.SettingsJson));
        });

        // Full replace. Engineers and Admins configure a project; SMEs and Observers read it.
        app.MapPut("/api/v1/corpora/{id:guid}/settings", async (
            Guid id,
            JsonElement body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (actor.Persona is not (Persona.Engineer or Persona.Admin))
                return Results.Json(
                    new { error = new { code = "auth.engineer_or_admin_required", message = "Project settings are changed by an Engineer or an Admin." } },
                    statusCode: StatusCodes.Status403Forbidden);

            var corpus = await db.Corpora.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (corpus is null) return Results.NotFound(new { error = new { code = "corpus.not_found" } });

            var (settings, errors) = ProjectSettings.Validate(body);
            if (settings is null)
                return Results.BadRequest(new
                {
                    error = new { code = "settings.invalid", message = string.Join("; ", errors), details = errors },
                });

            corpus.SettingsJson = settings.ToJson();
            // UpdatedAt is deliberately untouched: it orders the project list by data
            // activity, and a settings edit should not move a project to the top.
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "corpus.settings.updated", "corpus", corpus.Id, actor,
                new { artefactKinds = settings.ArtefactKinds, pack = settings.Pack, bannerSet = settings.Banner is not null },
                http, ct);

            return Results.Ok(settings);
        });

        return app;
    }
}
