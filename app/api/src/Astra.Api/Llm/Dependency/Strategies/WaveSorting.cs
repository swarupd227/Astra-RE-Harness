namespace Astra.Api.Llm.Dependency.Strategies;

/// <summary>
/// Shared topological-wave routines used by every Phase 8.0.e strategy.
/// Lives here so each strategy can override what it wants without
/// re-implementing the core graph traversal.
/// </summary>
internal static class WaveSorting
{
    /// <summary>
    /// Kahn's BFS over the SCC condensation. The same algorithm
    /// MigrationPlanner shipped in 8.0.b — moved here so every
    /// strategy can call it.
    /// </summary>
    public static List<List<Guid>> TopologicalWaves(DependencyGraph graph)
    {
        var (sccMembers, nodeToScc) = BuildScc(graph);
        var (condOut, callers) = BuildCondensation(graph, sccMembers, nodeToScc);
        var calleeRemaining = new Dictionary<int, int>();
        for (int i = 0; i < sccMembers.Count; i++)
            calleeRemaining[i] = condOut[i].Count;

        var waves = new List<List<Guid>>();
        var current = Enumerable.Range(0, sccMembers.Count)
            .Where(i => calleeRemaining[i] == 0)
            .ToList();
        var placed = new HashSet<int>();
        while (current.Count > 0)
        {
            var waveRoutines = current
                .SelectMany(i => sccMembers[i])
                .OrderBy(g => g)
                .ToList();
            waves.Add(waveRoutines);
            foreach (var i in current) placed.Add(i);

            var next = new HashSet<int>();
            foreach (var i in current)
            {
                foreach (var caller in callers[i])
                {
                    if (placed.Contains(caller)) continue;
                    calleeRemaining[caller]--;
                    if (calleeRemaining[caller] == 0) next.Add(caller);
                }
            }
            current = next.ToList();
        }

        var unplaced = Enumerable.Range(0, sccMembers.Count)
            .Where(i => !placed.Contains(i))
            .SelectMany(i => sccMembers[i])
            .OrderBy(g => g)
            .ToList();
        if (unplaced.Count > 0) waves.Add(unplaced);

        return waves;
    }

    /// <summary>
    /// Constrained topological sort: a routine is placed only when all
    /// its callees in <paramref name="placedCalleeRoutines"/> are in
    /// the placed set. Within the eligible set on each wave, routines
    /// are ordered by the caller-supplied <paramref name="comparer"/>
    /// — that's how higher strategies (business-priority, risk-first)
    /// inject their priority into the wave assignment.
    ///
    /// Wave 1 = routines whose callees are all in
    /// <paramref name="initiallyPlaced"/> (an empty set means "true
    /// leaves only"; callers can pre-place certain routines, e.g. a
    /// pilot set, to force them into Wave 1 regardless of topology).
    /// </summary>
    public static List<List<Guid>> ConstrainedTopologicalWaves(
        DependencyGraph graph,
        IEnumerable<Guid> initiallyPlaced,
        IComparer<Guid>? comparer = null)
    {
        comparer ??= Comparer<Guid>.Default;

        var (sccMembers, nodeToScc) = BuildScc(graph);
        var (condOut, callers) = BuildCondensation(graph, sccMembers, nodeToScc);
        var calleeRemaining = new Dictionary<int, int>();
        for (int i = 0; i < sccMembers.Count; i++)
            calleeRemaining[i] = condOut[i].Count;

        // Map pre-placed routines to their SCCs.
        var preplacedSccs = new HashSet<int>();
        foreach (var rid in initiallyPlaced)
            if (nodeToScc.TryGetValue(rid, out var s)) preplacedSccs.Add(s);

        var waves = new List<List<Guid>>();
        var placed = new HashSet<int>();
        // First "Wave 1": the pre-placed set (plus their cycle-mates).
        // We always emit a Wave 1 with the pre-placed routines, even
        // if their callees aren't yet placed — this is the strategy's
        // explicit override.
        if (preplacedSccs.Count > 0)
        {
            var first = preplacedSccs
                .SelectMany(i => sccMembers[i])
                .OrderBy(g => g, comparer)
                .ToList();
            waves.Add(first);
            foreach (var i in preplacedSccs) placed.Add(i);
            // Decrement remaining-counts for callers of placed SCCs.
            foreach (var i in preplacedSccs)
                foreach (var caller in callers[i])
                    if (!placed.Contains(caller)) calleeRemaining[caller]--;
        }

        // Standard Kahn BFS from here.
        var current = Enumerable.Range(0, sccMembers.Count)
            .Where(i => !placed.Contains(i) && calleeRemaining[i] == 0)
            .ToList();
        while (current.Count > 0)
        {
            var waveRoutines = current
                .SelectMany(i => sccMembers[i])
                .OrderBy(g => g, comparer)
                .ToList();
            waves.Add(waveRoutines);
            foreach (var i in current) placed.Add(i);

            var next = new HashSet<int>();
            foreach (var i in current)
                foreach (var caller in callers[i])
                {
                    if (placed.Contains(caller)) continue;
                    calleeRemaining[caller]--;
                    if (calleeRemaining[caller] == 0) next.Add(caller);
                }
            current = next.ToList();
        }

        var unplaced = Enumerable.Range(0, sccMembers.Count)
            .Where(i => !placed.Contains(i))
            .SelectMany(i => sccMembers[i])
            .OrderBy(g => g, comparer)
            .ToList();
        if (unplaced.Count > 0) waves.Add(unplaced);

        return waves;
    }

    // ── Internal SCC + condensation helpers ──

    private static (List<List<Guid>> sccMembers, Dictionary<Guid, int> nodeToScc)
        BuildScc(DependencyGraph graph)
    {
        var nodeToScc = new Dictionary<Guid, int>();
        var sccMembers = new List<List<Guid>>();
        int sccIdx = 0;
        foreach (var s in graph.Sccs)
        {
            sccMembers.Add(s.Members.ToList());
            foreach (var m in s.Members) nodeToScc[m] = sccIdx;
            sccIdx++;
        }
        foreach (var n in graph.Nodes)
        {
            if (nodeToScc.ContainsKey(n.Id)) continue;
            sccMembers.Add(new List<Guid> { n.Id });
            nodeToScc[n.Id] = sccIdx++;
        }
        return (sccMembers, nodeToScc);
    }

    private static (Dictionary<int, HashSet<int>> condOut, Dictionary<int, List<int>> callers)
        BuildCondensation(
            DependencyGraph graph,
            List<List<Guid>> sccMembers,
            Dictionary<Guid, int> nodeToScc)
    {
        var condOut = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < sccMembers.Count; i++) condOut[i] = new HashSet<int>();
        foreach (var e in graph.Edges)
        {
            if (e.Type != "call") continue;
            if (!nodeToScc.TryGetValue(e.From, out var fromS)) continue;
            if (!nodeToScc.TryGetValue(e.To, out var toS)) continue;
            if (fromS == toS) continue;
            condOut[fromS].Add(toS);
        }
        var callers = new Dictionary<int, List<int>>();
        for (int i = 0; i < sccMembers.Count; i++) callers[i] = new List<int>();
        foreach (var (from, tos) in condOut)
            foreach (var to in tos) callers[to].Add(from);
        return (condOut, callers);
    }
}
