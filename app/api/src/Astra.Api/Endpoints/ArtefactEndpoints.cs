using Astra.Api.Artefacts;

namespace Astra.Api.Endpoints;

public static class ArtefactEndpoints
{
    public static IEndpointRouteBuilder MapArtefactEndpoints(this IEndpointRouteBuilder app)
    {
        // The registry of artefact kinds a project can take. Read-only and ungated:
        // the New-project page and the settings page build their choices from it.
        app.MapGet("/api/v1/artefact-kinds", () =>
            Results.Ok(new { data = ArtefactKinds.All.Select(Describe).ToList() }));

        return app;
    }

    internal static object Describe(ArtefactKind k) => new
    {
        id = k.Id,
        label = k.Label,
        description = k.Description,
        extensions = k.Extensions,
        trustTier = k.Trust.ToString().ToLowerInvariant(),
        maxBytes = k.MaxBytes,
        text = k.IsText,
        core = k.Core,
    };
}
