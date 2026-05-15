using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Prompts;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #3b — prompt asset library discovery surface, plus Phase #4.3
/// admin-CRUD mutations.
///
///   GET    /api/v1/prompts                                       list all loaded prompts
///   GET    /api/v1/prompts/{source}/{target}/{kind}              latest version
///   GET    /api/v1/prompts/{source}/{target}/{kind}/{version}    pinned version (full body)
///
///   POST   /api/v1/prompts                                       admin-only, create a new version
///   PUT    /api/v1/prompts/{source}/{target}/{kind}/{version}    admin-only, overwrite a version
///   DELETE /api/v1/prompts/{source}/{target}/{kind}/{version}    admin-only, remove a version
/// </summary>
public static class PromptEndpoints
{
    public static IEndpointRouteBuilder MapPromptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/prompts", (PromptLibrary lib) =>
        {
            return Results.Ok(new
            {
                data = lib.All().Select(p => new
                {
                    sourceSchema = p.SourceSchema,
                    targetStack = p.TargetStack,
                    kind = p.Kind,
                    version = p.Version,
                    promptId = p.PromptId,
                    owner = p.Frontmatter.GetValueOrDefault("owner"),
                    status = p.Frontmatter.GetValueOrDefault("status"),
                    modelPreference = p.Frontmatter.GetValueOrDefault("modelPreference"),
                    path = p.Path,
                }),
            });
        });

        app.MapGet("/api/v1/prompts/{source}/{target}/{kind}", (
            string source, string target, string kind, PromptLibrary lib) =>
        {
            var p = lib.GetLatest(source, target, kind);
            if (p is null) return Results.NotFound(new { error = new { code = "prompt.not_found" } });
            return Results.Ok(Render(p));
        });

        app.MapGet("/api/v1/prompts/{source}/{target}/{kind}/{version}", (
            string source, string target, string kind, string version, PromptLibrary lib) =>
        {
            var p = lib.Get(source, target, kind, version);
            if (p is null) return Results.NotFound(new { error = new { code = "prompt.not_found" } });
            return Results.Ok(Render(p));
        });

        // ─── Phase #4.3 Admin mutations ──────────────────────────────────

        app.MapPost("/api/v1/prompts", async (
            CreatePromptRequest body,
            PromptLibrary lib,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(body.Markdown))
                return Results.BadRequest(new { error = new { code = "prompt.empty_body", message = "markdown is required." } });
            try
            {
                var saved = lib.SaveAndLoad(
                    body.SourceSchema, body.TargetStack, body.Kind, body.Version,
                    body.Markdown, overwriteExisting: false);

                await audit.LogAsync("prompt.created", "prompt", null, actor, payload: new
                {
                    sourceSchema = saved.SourceSchema,
                    targetStack = saved.TargetStack,
                    kind = saved.Kind,
                    version = saved.Version,
                    promptId = saved.PromptId,
                }, ctx, ct);

                return Results.Created($"/api/v1/prompts/{saved.SourceSchema}/{saved.TargetStack}/{saved.Kind}/{saved.Version}", Render(saved));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { code = "prompt.invalid_path", message = ex.Message } });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = new { code = "prompt.invalid", message = ex.Message } });
            }
        });

        app.MapPut("/api/v1/prompts/{source}/{target}/{kind}/{version}", async (
            string source, string target, string kind, string version,
            UpdatePromptRequest body,
            PromptLibrary lib,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(body.Markdown))
                return Results.BadRequest(new { error = new { code = "prompt.empty_body", message = "markdown is required." } });
            try
            {
                var saved = lib.SaveAndLoad(source, target, kind, version, body.Markdown, overwriteExisting: true);

                await audit.LogAsync("prompt.updated", "prompt", null, actor, payload: new
                {
                    sourceSchema = saved.SourceSchema,
                    targetStack = saved.TargetStack,
                    kind = saved.Kind,
                    version = saved.Version,
                    promptId = saved.PromptId,
                }, ctx, ct);

                return Results.Ok(Render(saved));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { code = "prompt.invalid_path", message = ex.Message } });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = new { code = "prompt.invalid", message = ex.Message } });
            }
        });

        app.MapDelete("/api/v1/prompts/{source}/{target}/{kind}/{version}", async (
            string source, string target, string kind, string version,
            PromptLibrary lib,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            try
            {
                var removed = lib.DeletePrompt(source, target, kind, version);
                if (!removed) return Results.NotFound(new { error = new { code = "prompt.not_found" } });
                await audit.LogAsync("prompt.deleted", "prompt", null, actor, payload: new
                {
                    sourceSchema = source,
                    targetStack = target,
                    kind,
                    version,
                }, ctx, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { code = "prompt.invalid_path", message = ex.Message } });
            }
        });

        return app;
    }

    private static object Render(PromptLibrary.LoadedPrompt p)
    {
        // Read the raw markdown body off disk so the FE can populate an
        // edit form. If the file went missing between save and read (rare),
        // fall back to null so the API still returns a valid response.
        string? body = null;
        try { if (File.Exists(p.Path)) body = File.ReadAllText(p.Path); }
        catch { /* ignore — body is best-effort */ }

        return new
        {
            sourceSchema = p.SourceSchema,
            targetStack = p.TargetStack,
            kind = p.Kind,
            version = p.Version,
            promptId = p.PromptId,
            frontmatter = p.Frontmatter,
            systemTemplate = p.SystemTemplate,
            userTemplate = p.UserTemplate,
            path = p.Path,
            body,
        };
    }

    private sealed record CreatePromptRequest(
        string SourceSchema,
        string TargetStack,
        string Kind,
        string Version,
        string Markdown);

    private sealed record UpdatePromptRequest(string Markdown);
}
