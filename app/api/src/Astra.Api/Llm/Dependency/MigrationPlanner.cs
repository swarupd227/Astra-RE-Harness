using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm.Dependency;

/// <summary>
/// Phase 8.0.b — Generates and manages dependency-aware migration
/// plans for a corpus.
///
/// Algorithm (the v1 strategy, "topological-leaves-first"):
///   1. Build the DependencyGraph via DependencyGraphBuilder.
///   2. Treat each SCC (cycle) as a single super-node — the whole
///      cycle migrates together.
///   3. Compute in-degree of each super-node in the condensation
///      DAG (call edges only; external callees + shared-storage
///      edges don't drive wave order in v1).
///   4. Kahn's BFS:
///        - Wave 1 = super-nodes with in-degree 0 (leaves, in the
///          "no in-corpus callees" sense)
///        - For each subsequent wave: remove the previous wave's
///          super-nodes; the next wave = super-nodes whose in-degree
///          dropped to 0.
///   5. Expand each super-node back to its constituent subroutine
///      IDs; sort alphabetically within an SCC for predictability.
///
/// The shared-storage edges from Phase 8.0.a are NOT used as wave-
/// order constraints in this strategy — they're surfaced as
/// readiness flags in 8.0.c instead. The wave assignment is
/// deterministic given the call graph; the same corpus produces
/// the same plan every time.
/// </summary>
public sealed class MigrationPlanner
{
    public const string DefaultStrategy = "topological-leaves-first";

    private readonly AppDbContext _db;
    private readonly DependencyGraphBuilder _graphBuilder;
    private readonly IAuditLogger _audit;
    private readonly ILogger<MigrationPlanner> _log;

    public MigrationPlanner(
        AppDbContext db,
        DependencyGraphBuilder graphBuilder,
        IAuditLogger audit,
        ILogger<MigrationPlanner> log)
    {
        _db = db;
        _graphBuilder = graphBuilder;
        _audit = audit;
        _log = log;
    }

    /// <summary>
    /// Generate a NEW draft plan from the current state of the call
    /// graph. Persists the plan + its waves but does NOT auto-approve.
    /// Any prior approved plan for the same (corpusId, sourceVersionId)
    /// stays approved until <see cref="ApproveAsync"/> is called on the
    /// new draft.
    /// </summary>
    public async Task<MigrationPlan> GenerateDraftAsync(
        Guid corpusId,
        string strategyName,
        DevPersonaContext? actor,
        CancellationToken ct = default)
    {
        if (strategyName != DefaultStrategy)
            throw new ArgumentException(
                $"Strategy '{strategyName}' not supported in 8.0.b. " +
                $"Phase 8.0.e ships additional strategies. v1 ships only '{DefaultStrategy}'.");

        var graph = await _graphBuilder.BuildAsync(corpusId, ct)
            ?? throw new InvalidOperationException(
                $"Corpus {corpusId} has no source versions; cannot generate plan.");

        var waveAssignments = AssignWavesTopological(graph);

        var plan = new MigrationPlan
        {
            Id = Guid.NewGuid(),
            CorpusId = corpusId,
            SourceVersionId = graph.SourceVersionId,
            Status = "draft",
            StrategyName = strategyName,
            StrategyOptionsJson = "{}",
            TotalRoutines = graph.Nodes.Count,
            TotalWaves = waveAssignments.Count,
            Summary = BuildSummary(waveAssignments.Count, graph.Nodes.Count, graph.Stats.CyclicSccCount),
            GeneratedBy = actor?.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.MigrationPlans.Add(plan);

        var waves = waveAssignments.Select((routineIds, idx) =>
        {
            var n = routineIds.Count;
            return new MigrationWave
            {
                Id = Guid.NewGuid(),
                MigrationPlanId = plan.Id,
                WaveNumber = idx + 1,
                Name = BuildWaveName(idx + 1, n, idx == 0, idx == waveAssignments.Count - 1),
                PlannedRoutineIdsJson = JsonSerializer.Serialize(routineIds.Select(g => g.ToString())),
                Status = "planned",
                RoutineCount = n,
            };
        }).ToList();
        _db.MigrationWaves.AddRange(waves);

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            "migration_plan.generated", "corpus", corpusId, actor,
            payload: new
            {
                planId = plan.Id,
                strategyName,
                totalRoutines = plan.TotalRoutines,
                totalWaves = plan.TotalWaves,
            },
            ct: ct);
        _log.LogInformation(
            "Generated migration plan {Plan} for corpus {Corpus}: {Routines} routines, {Waves} waves",
            plan.Id, corpusId, plan.TotalRoutines, plan.TotalWaves);
        return plan;
    }

    /// <summary>
    /// Approve a draft plan. Atomically archives any existing approved
    /// plan for the same (corpusId, sourceVersionId) tuple.
    /// </summary>
    public async Task<MigrationPlan> ApproveAsync(
        Guid planId, DevPersonaContext? actor, CancellationToken ct = default)
    {
        var plan = await _db.MigrationPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new InvalidOperationException($"Migration plan {planId} not found.");
        if (plan.Status != "draft")
            throw new InvalidOperationException($"Plan {planId} is in status '{plan.Status}'; only drafts can be approved.");

        var now = DateTimeOffset.UtcNow;
        // Archive any existing approved plan for the same (corpus, version).
        var prior = await _db.MigrationPlans
            .Where(p => p.CorpusId == plan.CorpusId
                     && p.SourceVersionId == plan.SourceVersionId
                     && p.Status == "approved"
                     && p.Id != planId)
            .ToListAsync(ct);
        foreach (var p in prior)
        {
            p.Status = "archived";
            p.ArchivedAt = now;
        }

        plan.Status = "approved";
        plan.ApprovedAt = now;
        plan.ApprovedBy = actor?.DisplayName;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "migration_plan.approved", "migration_plan", plan.Id, actor,
            payload: new
            {
                corpusId = plan.CorpusId,
                supersededPlanIds = prior.Select(p => p.Id).ToArray(),
            },
            ct: ct);
        return plan;
    }

    public async Task<MigrationPlan> ArchiveAsync(
        Guid planId, DevPersonaContext? actor, CancellationToken ct = default)
    {
        var plan = await _db.MigrationPlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new InvalidOperationException($"Migration plan {planId} not found.");
        if (plan.Status == "archived")
            return plan;
        plan.Status = "archived";
        plan.ArchivedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            "migration_plan.archived", "migration_plan", plan.Id, actor,
            payload: new { corpusId = plan.CorpusId },
            ct: ct);
        return plan;
    }

    /// <summary>
    /// Topological wave assignment via SCC condensation + Kahn's BFS.
    /// </summary>
    private static List<List<Guid>> AssignWavesTopological(DependencyGraph graph)
    {
        // 1. Build SCC membership lookup. Routines NOT in any SCC are
        //    treated as singleton super-nodes.
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

        // 2. Build condensation: super-node → set of callee super-nodes.
        var condOut = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < sccMembers.Count; i++) condOut[i] = new HashSet<int>();
        foreach (var e in graph.Edges)
        {
            if (e.Type != "call") continue;
            if (!nodeToScc.TryGetValue(e.From, out var fromS)) continue;
            if (!nodeToScc.TryGetValue(e.To, out var toS)) continue;
            if (fromS == toS) continue; // intra-SCC edge
            condOut[fromS].Add(toS);
        }

        // 3. Compute condensation in-degrees (i.e. who has callees not
        //    yet placed). Note the direction: if A CALLS B, then A's
        //    plan-time "dependency" is on B being ready first.
        //    So we want: a super-node is ready when all its CALLEES are placed.
        //    In Kahn terms: traverse the REVERSE of the call edge.
        //    Wave 1 = super-nodes whose callee set is empty (true leaves).
        var calleeRemaining = new Dictionary<int, int>();
        for (int i = 0; i < sccMembers.Count; i++)
            calleeRemaining[i] = condOut[i].Count;

        // Reverse adjacency: caller list per super-node.
        var callers = new Dictionary<int, List<int>>();
        for (int i = 0; i < sccMembers.Count; i++) callers[i] = new List<int>();
        foreach (var (from, tos) in condOut)
            foreach (var to in tos) callers[to].Add(from);

        // 4. Kahn's BFS by waves.
        var waves = new List<List<Guid>>();
        var current = Enumerable.Range(0, sccMembers.Count)
            .Where(i => calleeRemaining[i] == 0)
            .ToList();
        var placed = new HashSet<int>();
        while (current.Count > 0)
        {
            // Wave members in the original graph order, alphabetic by
            // routine name for SCC ties.
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

        // 5. Defensive: if there are super-nodes we couldn't place
        //    (shouldn't happen after SCC condensation eliminates
        //    cycles, but if the graph has a bug, surface as a final
        //    wave so the plan covers every routine).
        var unplaced = Enumerable.Range(0, sccMembers.Count)
            .Where(i => !placed.Contains(i))
            .SelectMany(i => sccMembers[i])
            .OrderBy(g => g)
            .ToList();
        if (unplaced.Count > 0) waves.Add(unplaced);

        return waves;
    }

    private static string BuildWaveName(int waveNumber, int routineCount, bool isFirst, bool isLast)
    {
        var qualifier = isFirst
            ? "leaf routines (no in-corpus callees)"
            : isLast
                ? "top-level orchestration"
                : "routines";
        return $"Wave {waveNumber} · {routineCount} {qualifier}";
    }

    private static string BuildSummary(int waveCount, int routineCount, int cycleCount)
    {
        var cycles = cycleCount > 0
            ? $", {cycleCount} cycle{(cycleCount == 1 ? "" : "s")} treated as super-node"
            : "";
        return $"{waveCount} wave{(waveCount == 1 ? "" : "s")}, {routineCount} routine{(routineCount == 1 ? "" : "s")}{cycles}.";
    }
}
