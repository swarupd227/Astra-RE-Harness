using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm.Dependency;

/// <summary>
/// Phase 8.0.a — Assembles a <see cref="DependencyGraph"/> for a single
/// corpus's latest <see cref="SourceVersion"/>. Same pattern as
/// <see cref="Astra.Api.Llm.NeighbourhoodBuilder"/>: one round-trip to
/// pull every subroutine + every existing spec, then in-memory graph
/// construction + Tarjan's SCC algorithm.
///
/// Why we don't materialise this to a table:
///   - The graph is derived state — pure function of (subroutine rows
///     + spec rows) at a point in time. Persisting it would mean
///     invalidating on every state change.
///   - The CONSUME_ROLL seed has 7 nodes / 9 edges. A 1000-routine
///     corpus has ~3000 edges. In-memory build is sub-second; HTTP
///     serialisation is the dominant cost. Caching at the HTTP layer
///     (ETag on (corpus_id, source_version_id, max(updated_at))) is
///     the right optimisation when it's needed.
/// </summary>
public sealed class DependencyGraphBuilder
{
    private readonly AppDbContext _db;
    private readonly ILogger<DependencyGraphBuilder> _log;

    public DependencyGraphBuilder(AppDbContext db, ILogger<DependencyGraphBuilder> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Build the graph for the latest source-version of the given corpus.</summary>
    public async Task<DependencyGraph?> BuildAsync(Guid corpusId, CancellationToken ct = default)
    {
        var version = await _db.SourceVersions.AsNoTracking()
            .Where(v => v.CorpusId == corpusId)
            .OrderByDescending(v => v.IngestedAt)
            .FirstOrDefaultAsync(ct);
        if (version is null) return null;
        return await BuildForVersionAsync(corpusId, version.Id, ct);
    }

    public async Task<DependencyGraph> BuildForVersionAsync(
        Guid corpusId, Guid sourceVersionId, CancellationToken ct = default)
    {
        // 1. Pull every subroutine in the version. Single query; we
        //    work in memory from here.
        var subs = await _db.Subroutines.AsNoTracking()
            .Include(s => s.SourceFile)
            .Where(s => s.SourceFile!.SourceVersionId == sourceVersionId)
            .ToListAsync(ct);

        // 2. Pull every spec for those subroutines (latest per sub).
        var subIds = subs.Select(s => s.Id).ToList();
        var specs = await _db.Specs.AsNoTracking()
            .Where(sp => subIds.Contains(sp.SubroutineId))
            .OrderByDescending(sp => sp.UpdatedAt)
            .ToListAsync(ct);
        var latestSpecBySub = specs
            .GroupBy(sp => sp.SubroutineId)
            .ToDictionary(g => g.Key, g => g.First());

        // 3. Pull every scaffold for those specs.
        var specIds = specs.Select(sp => sp.Id).ToList();
        var scaffolds = await _db.Scaffolds.AsNoTracking()
            .Where(sc => specIds.Contains(sc.SpecId))
            .OrderByDescending(sc => sc.GeneratedAt)
            .ToListAsync(ct);
        var latestScaffoldBySpec = scaffolds
            .GroupBy(sc => sc.SpecId)
            .ToDictionary(g => g.Key, g => g.First());

        // 4. Name → Subroutine lookup for callee resolution. Case-
        //    insensitive because COBOL is case-flexible and the parser
        //    upper-cases names.
        var byName = subs
            .GroupBy(s => s.Name.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        // 5. Build call edges + collect external callees.
        var callEdges = new List<GraphEdge>();
        var externalCallees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var calleeCount = new Dictionary<Guid, int>();
        var callerCount = new Dictionary<Guid, int>();
        foreach (var sub in subs)
        {
            var calls = ReadNameList(sub.CalledSubroutines);
            int resolvedCount = 0;
            foreach (var rawCall in calls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var nameUp = rawCall.ToUpperInvariant();
                if (byName.TryGetValue(nameUp, out var target))
                {
                    if (target.Id == sub.Id) continue; // skip self-recursion edges
                    callEdges.Add(new GraphEdge(sub.Id, target.Id, "call", null));
                    callerCount[target.Id] = callerCount.GetValueOrDefault(target.Id) + 1;
                    resolvedCount++;
                }
                else
                {
                    externalCallees.Add(rawCall);
                }
            }
            calleeCount[sub.Id] = resolvedCount;
        }

        // 6. Build shared-storage edges. Heuristic: every routine that
        //    references block /B/ is paired with every OTHER routine
        //    that references /B/. Direction is unsourced for COMMON
        //    (the parser doesn't tell us write vs read), so we emit
        //    A→B for the alphabetically-earlier-named routine and let
        //    the UI render it as bidirectional if needed.
        var sharedStorageEdges = new List<GraphEdge>();
        var blockToRoutines = new Dictionary<string, List<Subroutine>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in subs)
        {
            foreach (var blockName in ReadNameList(sub.CommonBlockRefs))
            {
                if (!blockToRoutines.TryGetValue(blockName, out var list))
                {
                    list = new List<Subroutine>();
                    blockToRoutines[blockName] = list;
                }
                list.Add(sub);
            }
        }
        foreach (var (block, members) in blockToRoutines)
        {
            if (members.Count < 2) continue;
            var ordered = members.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    sharedStorageEdges.Add(new GraphEdge(
                        From: ordered[i].Id, To: ordered[j].Id,
                        Type: "shared-storage", ViaBlock: block));
                }
            }
        }

        // 7. Tarjan's SCC over call edges only. Shared-storage doesn't
        //    create a wave-ordering cycle.
        var sccs = TarjanScc.Compute(
            subs.Select(s => s.Id).ToList(),
            callEdges);
        var sccBySub = new Dictionary<Guid, string>();
        var sccList = new List<StronglyConnectedComponent>();
        int sccCounter = 0;
        foreach (var component in sccs)
        {
            if (component.Count == 1) continue; // singleton SCCs not interesting
            sccCounter++;
            var id = $"scc-{sccCounter}";
            sccList.Add(new StronglyConnectedComponent(id, component));
            foreach (var memberId in component) sccBySub[memberId] = id;
        }

        // 8. Compose nodes with state classification.
        var nodes = subs
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s =>
            {
                Spec? spec = latestSpecBySub.GetValueOrDefault(s.Id);
                Scaffold? scaffold = spec is null
                    ? null
                    : latestScaffoldBySpec.GetValueOrDefault(spec.Id);
                var state = ClassifyState(s, spec, scaffold);
                var ce = calleeCount.GetValueOrDefault(s.Id);
                var cr = callerCount.GetValueOrDefault(s.Id);
                return new GraphNode(
                    Id: s.Id,
                    Name: s.Name,
                    SourcePath: s.SourceFile?.RelativePath ?? "",
                    State: state,
                    SpecId: spec?.Id,
                    IsRoot: cr == 0,
                    IsLeaf: ce == 0,
                    SccId: sccBySub.GetValueOrDefault(s.Id),
                    CalleeCount: ce,
                    CallerCount: cr);
            })
            .ToList();

        var allEdges = callEdges.Concat(sharedStorageEdges).ToList();
        var stats = new GraphStats(
            NodeCount: nodes.Count,
            CallEdgeCount: callEdges.Count,
            SharedStorageEdgeCount: sharedStorageEdges.Count,
            SccCount: sccList.Count,
            CyclicSccCount: sccList.Count,
            ExternalCalleeCount: externalCallees.Count,
            LeafCount: nodes.Count(n => n.IsLeaf),
            RootCount: nodes.Count(n => n.IsRoot));

        _log.LogInformation(
            "Dependency graph for corpus {Corpus}: {Nodes} nodes, {Call} call edges, {Shared} shared-storage edges, {Sccs} SCCs, {External} external callees",
            corpusId, stats.NodeCount, stats.CallEdgeCount, stats.SharedStorageEdgeCount, stats.SccCount, stats.ExternalCalleeCount);

        return new DependencyGraph(
            CorpusId: corpusId,
            SourceVersionId: sourceVersionId,
            Nodes: nodes,
            Edges: allEdges,
            ExternalCallees: externalCallees.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            Sccs: sccList,
            Stats: stats);
    }

    /// <summary>
    /// State classification — most-progressed wins. Order:
    /// COMMITTED > SCAFFOLDED > SIGNED > DRAFT > EXTRACTING > PARSED.
    /// </summary>
    private static string ClassifyState(Subroutine sub, Spec? spec, Scaffold? scaffold)
    {
        if (scaffold is not null && scaffold.State == "COMMITTED") return "COMMITTED";
        if (scaffold is not null) return "SCAFFOLDED";
        if (spec is not null && spec.State == "SIGNED") return "SIGNED";
        if (spec is not null) return spec.State; // DRAFT / IN_REVIEW / etc.
        return sub.State; // PARSED / EXTRACTING / etc.
    }

    private static IReadOnlyList<string> ReadNameList(JsonDocument? doc)
    {
        if (doc is null) return Array.Empty<string>();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}

/// <summary>
/// Tarjan's strongly-connected-components algorithm. Iterative
/// version — recursive Tarjan blows the .NET stack on graphs with
/// long call chains (legacy COBOL programs routinely have PERFORM
/// chains 50+ deep through copybooks).
/// </summary>
internal static class TarjanScc
{
    public static IReadOnlyList<IReadOnlyList<Guid>> Compute(
        IReadOnlyList<Guid> nodes,
        IReadOnlyList<GraphEdge> edges)
    {
        // Adjacency list keyed by node id.
        var adj = new Dictionary<Guid, List<Guid>>();
        foreach (var n in nodes) adj[n] = new List<Guid>();
        foreach (var e in edges)
        {
            if (e.Type != "call") continue;
            if (adj.TryGetValue(e.From, out var outs)) outs.Add(e.To);
        }

        var index = 0;
        var nodeIndex = new Dictionary<Guid, int>();
        var nodeLowlink = new Dictionary<Guid, int>();
        var onStack = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        var result = new List<IReadOnlyList<Guid>>();

        foreach (var v in nodes)
        {
            if (nodeIndex.ContainsKey(v)) continue;
            // Iterative DFS frame.
            // Each frame: (node, enumerator-state) — using indexed
            // iteration over adj[v] via a parallel stack of "next-
            // child-index-to-visit".
            var callStack = new Stack<(Guid Node, int NextChild)>();
            nodeIndex[v] = index;
            nodeLowlink[v] = index;
            index++;
            stack.Push(v);
            onStack.Add(v);
            callStack.Push((v, 0));

            while (callStack.Count > 0)
            {
                var (current, nextChild) = callStack.Peek();
                var children = adj[current];
                if (nextChild < children.Count)
                {
                    var w = children[nextChild];
                    // Bump the child cursor on the frame.
                    callStack.Pop();
                    callStack.Push((current, nextChild + 1));
                    if (!nodeIndex.ContainsKey(w))
                    {
                        nodeIndex[w] = index;
                        nodeLowlink[w] = index;
                        index++;
                        stack.Push(w);
                        onStack.Add(w);
                        callStack.Push((w, 0));
                    }
                    else if (onStack.Contains(w))
                    {
                        nodeLowlink[current] = Math.Min(nodeLowlink[current], nodeIndex[w]);
                    }
                }
                else
                {
                    // All children processed. Pop the frame; propagate
                    // lowlink to parent if any.
                    callStack.Pop();
                    if (nodeLowlink[current] == nodeIndex[current])
                    {
                        var component = new List<Guid>();
                        Guid popped;
                        do
                        {
                            popped = stack.Pop();
                            onStack.Remove(popped);
                            component.Add(popped);
                        } while (popped != current);
                        result.Add(component);
                    }
                    if (callStack.Count > 0)
                    {
                        var parent = callStack.Peek().Node;
                        nodeLowlink[parent] = Math.Min(nodeLowlink[parent], nodeLowlink[current]);
                    }
                }
            }
        }

        return result;
    }
}
