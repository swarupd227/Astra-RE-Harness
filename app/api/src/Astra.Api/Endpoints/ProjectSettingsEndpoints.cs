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

        // Rename a project. Engineers and Admins, like settings; names stay unique (case-insensitive) because the
        // projects list, the rail and every picker show the name alone.
        app.MapPut("/api/v1/corpora/{id:guid}/name", async (
            Guid id,
            RenameBody body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (actor.Persona is not (Persona.Engineer or Persona.Admin))
                return Results.Json(
                    new { error = new { code = "auth.engineer_or_admin_required", message = "A project is renamed by an Engineer or an Admin." } },
                    statusCode: StatusCodes.Status403Forbidden);

            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > 200)
                return Results.BadRequest(new { error = new { code = "corpus.name_invalid", message = "A name is 1 to 200 characters." } });

            var corpus = await db.Corpora.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (corpus is null) return Results.NotFound(new { error = new { code = "corpus.not_found" } });

            var lower = name.ToLower();
            if (await db.Corpora.AnyAsync(c => c.Id != id && c.Name.ToLower() == lower, ct))
                return Results.Conflict(new { error = new { code = "corpus.name_taken", message = "Another project already has this name." } });

            var old = corpus.Name;
            corpus.Name = name;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("corpus.renamed", "corpus", corpus.Id, actor, new { from = old, to = name }, http, ct);
            return Results.Ok(new { id = corpus.Id, name = corpus.Name });
        });

        return app;
    }

    public sealed record RenameBody(string? Name);
}
