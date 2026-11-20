using Astra.Api.Evidence;

namespace Astra.Api.Endpoints;

/// <summary>
/// The inventory of a project's current version: counts of what its evidence holds next to what the project
/// expects, and the gaps that follow. Open to every persona, like all evidence.
/// </summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/corpora/{id:guid}/inventory", async (
            Guid id, Guid? versionId, EvidenceService evidence, GraphService graph, InventoryService inventory, CancellationToken ct) =>
        {
            var (found, version) = await evidence.ResolveVersionAsync(id, versionId, ct);
            if (!found) return Results.NotFound(new { error = new { code = "corpus.not_found", message = "No such project." } });
            if (version is not { } v)
                return Results.NotFound(new { error = new { code = "inventory.no_version", message = "This project has no source version yet." } });

            await evidence.EnsureIndexedAsync(v, ct);
            await graph.EnsureBuiltAsync(v, ct);
            return Results.Ok(await inventory.GetAsync(id, v, ct));
        });

        return app;
    }
}
