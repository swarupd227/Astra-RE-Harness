using Astra.Api.Llm.Prompts;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #3b — prompt asset library discovery surface.
///
///   GET /api/v1/prompts                                       list all loaded prompts
///   GET /api/v1/prompts/{source}/{target}/{kind}              latest version
///   GET /api/v1/prompts/{source}/{target}/{kind}/{version}    pinned version (full body)
///
/// Public read-only. The body endpoint returns the rendered system/user
/// templates so a CIO walkthrough can show "this is the calibrated
/// system prompt we ship for COBOL → C#".
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

        return app;
    }

    private static object Render(PromptLibrary.LoadedPrompt p) => new
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
    };
}
