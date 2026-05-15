using Astra.Api.Llm.Archetypes;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #3c — scaffold archetype discovery surface.
///
///   GET /api/v1/archetypes                          list all loaded archetypes
///   GET /api/v1/archetypes/{target}/{id}            archetype detail + file list
///   GET /api/v1/archetypes/{target}/{id}/files/*    a single template file's content
///
/// Public read-only. Lets a CIO walkthrough show "here are the target
/// stacks we ship pre-built scaffolds for, here's exactly what files
/// each one produces, here's the code we'd generate before the engineer
/// fills it in."
/// </summary>
public static class ArchetypeEndpoints
{
    public static IEndpointRouteBuilder MapArchetypeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/archetypes", (ArchetypeRegistry reg) =>
        {
            return Results.Ok(new
            {
                data = reg.All().Select(a => new
                {
                    id = a.Manifest.Id,
                    targetStack = a.Manifest.TargetStack,
                    displayName = a.Manifest.DisplayName,
                    description = a.Manifest.Description,
                    compatibleSchemas = a.Manifest.CompatibleSchemas,
                    owner = a.Manifest.Owner,
                    status = a.Manifest.Status,
                    platformReadiness = a.Manifest.PlatformReadiness,
                    fileCount = a.Files.Count,
                    files = a.Files.Select(f => new
                    {
                        path = f.Path,
                        language = f.Language,
                        lineCount = f.LineCount,
                        todoCount = f.TodoCount,
                        derivedFromClaimIds = f.DerivedFromClaimIds,
                    }),
                }),
            });
        });

        app.MapGet("/api/v1/archetypes/{target}/{id}", (string target, string id, ArchetypeRegistry reg) =>
        {
            var a = reg.Get(target, id);
            if (a is null) return Results.NotFound(new { error = new { code = "archetype.not_found" } });
            return Results.Ok(new
            {
                id = a.Manifest.Id,
                targetStack = a.Manifest.TargetStack,
                displayName = a.Manifest.DisplayName,
                description = a.Manifest.Description,
                compatibleSchemas = a.Manifest.CompatibleSchemas,
                matches = a.Manifest.Matches,
                owner = a.Manifest.Owner,
                status = a.Manifest.Status,
                platformReadiness = a.Manifest.PlatformReadiness,
                files = a.Files.Select(f => new
                {
                    path = f.Path,
                    language = f.Language,
                    lineCount = f.LineCount,
                    todoCount = f.TodoCount,
                    derivedFromClaimIds = f.DerivedFromClaimIds,
                    content = f.Content,
                }),
            });
        });

        return app;
    }
}
