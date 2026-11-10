using Astra.Api.Evidence;

namespace Astra.Api.Endpoints;

/// <summary>
/// The entity graph of a project's current version: what its database scripts, job exports and packages
/// say exists, and how those things relate, each with the evidence that shows it. (The routine call graph
/// is a different thing, at /dependency-graph.) Reads are open to every persona, like all evidence.
/// </summary>
public static class EntityGraphEndpoints
{
    public static IEndpointRouteBuilder MapEntityGraphEndpoints(this IEndpointRouteBuilder app)
    {
        // Nodes and edges: ?type=job,table narrows what is shown (edges only between what is shown).
        app.MapGet("/api/v1/corpora/{id:guid}/entity-graph", async (
            Guid id, string? type, Guid? versionId, EvidenceService evidence, GraphService graph, CancellationToken ct) =>
        {
            var (found, version) = await evidence.ResolveVersionAsync(id, versionId, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v)
                return Results.Ok(new EntityGraphDto(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), Array.Empty<GraphTypeCount>()));

            await evidence.EnsureIndexedAsync(v, ct);
            await graph.EnsureBuiltAsync(v, ct);
            var types = string.IsNullOrWhiteSpace(type)
                ? null
                : type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Ok(await graph.GetGraphAsync(v, types, ct));
        });

        // The things found, a page at a time: ?type=&q=&limit=&offset=
        app.MapGet("/api/v1/corpora/{id:guid}/entities", async (
            Guid id, string? type, string? q, int? limit, int? offset, Guid? versionId,
            EvidenceService evidence, GraphService graph, CancellationToken ct) =>
        {
            var (found, version) = await evidence.ResolveVersionAsync(id, versionId, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return Results.Ok(new EntityPageDto(Array.Empty<GraphNode>(), 0, Array.Empty<GraphTypeCount>()));

            await evidence.EnsureIndexedAsync(v, ct);
            await graph.EnsureBuiltAsync(v, ct);
            return Results.Ok(await graph.ListAsync(v, type, q, limit ?? 50, offset ?? 0, ct));
        });

        // One thing and everything linked to it. The name may hold slashes (a file path), so it takes the rest of the path.
        app.MapGet("/api/v1/corpora/{id:guid}/entities/{type}/{**name}", async (
            Guid id, string type, string name, Guid? versionId, EvidenceService evidence, GraphService graph, CancellationToken ct) =>
        {
            var (found, version) = await evidence.ResolveVersionAsync(id, versionId, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return NotFound("entity.not_found", "This project has no source version yet.");

            await evidence.EnsureIndexedAsync(v, ct);
            await graph.EnsureBuiltAsync(v, ct);
            var detail = await graph.GetAsync(v, type, name, ct);
            return detail is null
                ? NotFound("entity.not_found", $"No {type} named '{name}' is in this version of the project.")
                : Results.Ok(detail);
        });

        return app;
    }

    private static IResult NotFound(string code, string message) =>
        Results.NotFound(new { error = new { code, message } });
}
