namespace Astra.Api.Llm.Dependency;

/// <summary>
/// Phase 8.0.a — In-memory dependency graph for a single corpus
/// source-version. Built by <see cref="DependencyGraphBuilder"/> from
/// parser-extracted call-graph + COMMON-block / copybook metadata
/// already on every <see cref="Persistence.Entities.Subroutine"/> row.
///
/// Two edge types: <c>call</c> (X → Y when X CALLs Y) and
/// <c>shared-storage</c> (X → Y when X writes to a COMMON block /
/// copybook that Y reads). Both surface in the graph; the wave-planner
/// (Phase 8.0.b) treats only call edges as wave-order constraints,
/// while the readiness classifier (Phase 8.0.c) uses shared-storage
/// edges to flag <c>coordinated-only</c> migrations.
/// </summary>
public sealed record DependencyGraph(
    Guid CorpusId,
    Guid SourceVersionId,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    /// <summary>Names referenced as CALL targets but not resolved to an
    /// in-corpus subroutine (system / OS / library / missing source).</summary>
    IReadOnlyList<string> ExternalCallees,
    /// <summary>Strongly-connected components from Tarjan's algorithm.
    /// Each SCC with >1 member is a cycle in the call graph (mutual
    /// recursion, common in legacy F77 PERFORM / GOTO patterns). The
    /// wave-planner treats each SCC as one super-node — the whole cycle
    /// migrates in a single wave.</summary>
    IReadOnlyList<StronglyConnectedComponent> Sccs,
    GraphStats Stats);

/// <summary>
/// One subroutine in the graph. Node ID == Subroutine.Id so the UI
/// can drill into /subroutines/{id} on click.
/// </summary>
public sealed record GraphNode(
    Guid Id,
    string Name,
    string SourcePath,
    /// <summary>
    /// Most-progressed state: COMMITTED (has committed scaffold) >
    /// SCAFFOLDED (has any scaffold) > SIGNED (has signed spec) >
    /// DRAFT / EXTRACTING / PARSED (subroutine state).
    /// Used by the UI to colour nodes by progression.
    /// </summary>
    string State,
    Guid? SpecId,
    bool IsRoot,
    bool IsLeaf,
    /// <summary>Membership in a strongly-connected component, if any.
    /// Null for nodes that aren't part of a cycle. Matches the
    /// <see cref="StronglyConnectedComponent.Id"/> field.</summary>
    string? SccId,
    int CalleeCount,
    int CallerCount);

public sealed record GraphEdge(
    Guid From,
    Guid To,
    /// <summary>"call" | "shared-storage".</summary>
    string Type,
    /// <summary>For shared-storage edges, the COMMON block name or
    /// copybook name that creates the coupling. Null for call edges.</summary>
    string? ViaBlock);

public sealed record StronglyConnectedComponent(
    string Id,
    IReadOnlyList<Guid> Members);

public sealed record GraphStats(
    int NodeCount,
    int CallEdgeCount,
    int SharedStorageEdgeCount,
    int SccCount,
    int CyclicSccCount,
    int ExternalCalleeCount,
    int LeafCount,
    int RootCount);
