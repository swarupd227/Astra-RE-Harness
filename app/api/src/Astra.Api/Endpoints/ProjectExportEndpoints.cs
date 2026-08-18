using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Export;

namespace Astra.Api.Endpoints;

/// <summary>
/// Project-level artifact bundle export.
///
///   GET /api/v1/corpora/{corpusId}/export?includeSources=true|false
///       Returns {name}-artifacts.zip with the docs tree, dependency-graph
///       JSON, migration-plan JSON, pattern-analysis JSON, and a manifest.
///       includeSources=true additionally bundles the original source tree,
///       latest scaffold packages, and validation logs (blob-backed, so the
///       zip can get large). Admin persona required — same posture as the
///       docs export.
/// </summary>
public static class ProjectExportEndpoints
{
    public static IEndpointRouteBuilder MapProjectExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/corpora/{corpusId:guid}/export", async (
            Guid corpusId,
            bool? includeSources,
            ProjectExportService exporter,
            IAuditLogger audit,
            DevPersonaContext actor,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var withSources = includeSources ?? false;
            try
            {
                var result = await exporter.ExportAsync(corpusId, withSources, ct);

                await audit.LogAsync(
                    "project.exported", "corpus", corpusId, actor,
                    payload: new { includeSources = withSources, fileName = result.FileName, bytes = result.Bytes.Length },
                    ctx, ct);

                return Results.File(result.Bytes, result.ContentType, fileDownloadName: result.FileName);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return Results.NotFound(new { error = new { code = "corpus.not_found" } });
            }
        });

        return app;
    }

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}
