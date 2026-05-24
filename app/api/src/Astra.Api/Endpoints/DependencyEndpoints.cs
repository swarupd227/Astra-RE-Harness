using Astra.Api.Auth;
using Astra.Api.Llm.Dependency;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 8.0.a — Dependency graph read surface.
///
///   GET /api/v1/corpora/{id}/dependency-graph
///       Returns the corpus's latest source-version graph: nodes,
///       call + shared-storage edges, SCCs, external callees, stats.
///       Open to every authenticated persona — the graph is part of
///       the audit trail and worth surfacing read-only to observers.
///
/// Future Phase 8.0.b/c/d/e endpoints land in this file too
/// (MigrationPlanEndpoints / BlastRadiusEndpoints / etc. are
/// candidates if it gets too long).
/// </summary>
public static class DependencyEndpoints
{
    public static IEndpointRouteBuilder MapDependencyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/corpora/{id:guid}/dependency-graph", async (
            Guid id,
            DependencyGraphBuilder builder,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var exists = await db.Corpora.AsNoTracking().AnyAsync(c => c.Id == id, ct);
            if (!exists)
                return Results.NotFound(new { error = new { code = "corpus.not_found" } });

            var graph = await builder.BuildAsync(id, ct);
            if (graph is null)
                return Results.NotFound(new { error = new { code = "corpus.no_source_version" } });

            return Results.Ok(RenderGraph(graph));
        });

        return app;
    }

    private static object RenderGraph(DependencyGraph g) => new
    {
        corpusId = g.CorpusId,
        sourceVersionId = g.SourceVersionId,
        nodes = g.Nodes.Select(n => new
        {
            id = n.Id,
            name = n.Name,
            sourcePath = n.SourcePath,
            state = n.State,
            specId = n.SpecId,
            isRoot = n.IsRoot,
            isLeaf = n.IsLeaf,
            sccId = n.SccId,
            calleeCount = n.CalleeCount,
            callerCount = n.CallerCount,
        }),
        edges = g.Edges.Select(e => new
        {
            from = e.From,
            to = e.To,
            type = e.Type,
            viaBlock = e.ViaBlock,
        }),
        externalCallees = g.ExternalCallees,
        sccs = g.Sccs.Select(s => new { id = s.Id, members = s.Members }),
        stats = new
        {
            nodeCount = g.Stats.NodeCount,
            callEdgeCount = g.Stats.CallEdgeCount,
            sharedStorageEdgeCount = g.Stats.SharedStorageEdgeCount,
            sccCount = g.Stats.SccCount,
            cyclicSccCount = g.Stats.CyclicSccCount,
            externalCalleeCount = g.Stats.ExternalCalleeCount,
            leafCount = g.Stats.LeafCount,
            rootCount = g.Stats.RootCount,
        },
    };
}
