using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm.Dependency;

/// <summary>
/// Phase 8.0.c — Per-routine "migration context": blast radius +
/// readiness classification + wave assignment. Reads the Phase 8.0.a
/// dependency graph and joins it with the Phase 8.0.b migration plan.
///
/// Service is intentionally a single class — blast radius and
/// readiness share the same graph build, so computing them together
/// is one round-trip instead of two.
/// </summary>
public sealed class MigrationContextService
{
    private readonly AppDbContext _db;
    private readonly DependencyGraphBuilder _graphBuilder;
    private readonly ILogger<MigrationContextService> _log;

    public MigrationContextService(
        AppDbContext db,
        DependencyGraphBuilder graphBuilder,
        ILogger<MigrationContextService> log)
    {
        _db = db;
        _graphBuilder = graphBuilder;
        _log = log;
    }

    /// <summary>
    /// Computes the transitive set of routines that depend on the
    /// given one — direct callers (1 hop), indirect callers (N hops),
    /// plus every routine that touches the same COMMON block /
    /// copybook this routine touches (shared-storage consumers, 1 hop
    /// only). Transitive amplification on shared storage is too noisy
    /// to be useful (almost everything touches /CFG/ in real corpora).
    /// </summary>
    public async Task<BlastRadius?> ComputeBlastRadiusAsync(Guid subroutineId, CancellationToken ct = default)
    {
        var (sub, graph) = await LoadGraphForSubroutineAsync(subroutineId, ct);
        if (sub is null || graph is null) return null;

        // Build reverse adjacency for the call graph: edge A→B (A calls B)
        // means B's caller set includes A. We're traversing UPWARD from
        // `sub` to find every routine that ultimately depends on it.
        var reverseCall = new Dictionary<Guid, List<Guid>>();
        foreach (var n in graph.Nodes) reverseCall[n.Id] = new List<Guid>();
        foreach (var e in graph.Edges)
        {
            if (e.Type != "call") continue;
            if (!reverseCall.TryGetValue(e.To, out var list)) continue;
            list.Add(e.From);
        }

        var directCallers = reverseCall[sub.Id].Distinct().ToList();
        var transitiveSet = new HashSet<Guid>();
        var queue = new Queue<Guid>(directCallers);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!transitiveSet.Add(node)) continue;
            if (reverseCall.TryGetValue(node, out var ups))
            {
                foreach (var u in ups) if (!transitiveSet.Contains(u)) queue.Enqueue(u);
            }
        }
        // transitiveSet now contains direct + indirect callers
        // (direct ones are also in the transitive set).

        // Shared-storage consumers: every OTHER routine that references
        // any COMMON block this routine references. We don't try to
        // distinguish read vs write — the parser doesn't tell us.
        var selfBlocks = ReadNameList(sub.CommonBlockRefs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedStorageConsumers = new Dictionary<Guid, List<string>>();
        if (selfBlocks.Count > 0)
        {
            var versionId = sub.SourceFile!.SourceVersionId;
            var others = await _db.Subroutines.AsNoTracking()
                .Include(s => s.SourceFile)
                .Where(s => s.SourceFile!.SourceVersionId == versionId
                         && s.Id != sub.Id)
                .ToListAsync(ct);
            foreach (var other in others)
            {
                var overlap = ReadNameList(other.CommonBlockRefs)
                    .Where(b => selfBlocks.Contains(b))
                    .ToList();
                if (overlap.Count > 0) sharedStorageConsumers[other.Id] = overlap;
            }
        }

        // Render affected routines with current state.
        var affectedIds = new HashSet<Guid>(transitiveSet);
        foreach (var id in sharedStorageConsumers.Keys) affectedIds.Add(id);

        var affectedNodes = graph.Nodes
            .Where(n => affectedIds.Contains(n.Id))
            .ToDictionary(n => n.Id);

        var directCallerSet = directCallers.ToHashSet();
        var affected = affectedIds
            .Select(id =>
            {
                var node = affectedNodes.GetValueOrDefault(id);
                var blocks = sharedStorageConsumers.GetValueOrDefault(id, new List<string>());
                return new BlastRadiusEntry(
                    Id: id,
                    Name: node?.Name ?? "(missing)",
                    State: node?.State ?? "MISSING",
                    Reason: directCallerSet.Contains(id) && blocks.Count > 0 ? "direct-caller+shared-storage" :
                            directCallerSet.Contains(id) ? "direct-caller" :
                            transitiveSet.Contains(id) ? "transitive-caller" :
                            "shared-storage",
                    SharedBlocks: blocks);
            })
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        // State breakdown for the summary badge.
        int signed = 0, scaffolded = 0, draft = 0, parsed = 0;
        foreach (var r in affected)
        {
            switch (r.State)
            {
                case "SIGNED": signed++; break;
                case "SCAFFOLDED":
                case "COMMITTED": scaffolded++; break;
                case "DRAFT":
                case "IN_REVIEW": draft++; break;
                default: parsed++; break;
            }
        }

        return new BlastRadius(
            SubroutineId: sub.Id,
            SubroutineName: sub.Name,
            DirectCallerCount: directCallers.Count,
            TransitiveCallerCount: transitiveSet.Count,
            SharedStorageConsumerCount: sharedStorageConsumers.Count,
            Affected: affected,
            StateBreakdown: new StateBreakdown(signed, scaffolded, draft, parsed));
    }

    /// <summary>
    /// Decision-tree classification. Order matters — first match wins.
    /// </summary>
    public async Task<MigrationReadiness?> ClassifyAsync(Guid subroutineId, CancellationToken ct = default)
    {
        var (sub, graph) = await LoadGraphForSubroutineAsync(subroutineId, ct);
        if (sub is null || graph is null) return null;

        var node = graph.Nodes.FirstOrDefault(n => n.Id == sub.Id);
        if (node is null) return null;

        // Pull self's call graph + COMMON refs from the graph result.
        var calleeIds = graph.Edges
            .Where(e => e.Type == "call" && e.From == sub.Id)
            .Select(e => e.To)
            .Distinct()
            .ToList();
        var callerIds = graph.Edges
            .Where(e => e.Type == "call" && e.To == sub.Id)
            .Select(e => e.From)
            .Distinct()
            .ToList();

        var calleeStateById = graph.Nodes
            .Where(n => calleeIds.Contains(n.Id))
            .ToDictionary(n => n.Id, n => n.State);

        // Shared-storage check: does THIS routine reference any COMMON
        // block that other routines in the same version reference?
        var selfBlocks = ReadNameList(sub.CommonBlockRefs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasSharedBlocks = false;
        var sharedBlockNames = new List<string>();
        if (selfBlocks.Count > 0)
        {
            var versionId = sub.SourceFile!.SourceVersionId;
            var others = await _db.Subroutines.AsNoTracking()
                .Where(s => s.SourceFile!.SourceVersionId == versionId
                         && s.Id != sub.Id)
                .Select(s => s.CommonBlockRefs)
                .ToListAsync(ct);
            var allOtherBlocks = others
                .SelectMany(doc => ReadNameList(doc))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            sharedBlockNames = selfBlocks.Where(b => allOtherBlocks.Contains(b)).ToList();
            hasSharedBlocks = sharedBlockNames.Count > 0;
        }

        var reasons = new List<string>();
        var blockingRoutineIds = new List<Guid>();
        string classification;

        // ── Decision tree (first match wins) ──
        if (hasSharedBlocks)
        {
            classification = "coordinated-only";
            reasons.Add($"References shared storage block(s) {string.Join(", ", sharedBlockNames.Select(b => $"/{b}/"))} also touched by other routines in this corpus. Migration must co-locate or replicate the shared-state contract.");
        }
        else
        {
            var unsignedCallees = calleeStateById
                .Where(kv => kv.Value != "SIGNED" && kv.Value != "SCAFFOLDED" && kv.Value != "COMMITTED")
                .Select(kv => kv.Key)
                .ToList();
            if (unsignedCallees.Count > 0)
            {
                classification = "blocked-on-deps";
                blockingRoutineIds.AddRange(unsignedCallees);
                reasons.Add($"Has {unsignedCallees.Count} callee{(unsignedCallees.Count == 1 ? "" : "s")} not yet SIGNED in this corpus. Migrating now would extract against unsigned-spec dependencies.");
            }
            else if (calleeIds.Count > 0)
            {
                classification = "safe-with-deps-done";
                reasons.Add($"All {calleeIds.Count} in-corpus callee{(calleeIds.Count == 1 ? "" : "s")} already SIGNED or further. Safe to migrate now.");
            }
            else
            {
                classification = "safe-leaf";
                reasons.Add("No in-corpus callees and no shared-storage coupling. Migrate independently.");
            }
        }

        return new MigrationReadiness(
            SubroutineId: sub.Id,
            SubroutineName: sub.Name,
            Classification: classification,
            Reasons: reasons,
            BlockingRoutineIds: blockingRoutineIds,
            CalleeCount: calleeIds.Count,
            CallerCount: callerIds.Count,
            SharedBlockNames: sharedBlockNames);
    }

    /// <summary>
    /// Find this routine's assignment in the corpus's CURRENT migration
    /// plan (approved if exists, else latest draft). Returns null if no
    /// plan exists for the corpus.
    /// </summary>
    public async Task<WaveAssignment?> GetWaveAssignmentAsync(Guid subroutineId, CancellationToken ct = default)
    {
        var sub = await _db.Subroutines.AsNoTracking()
            .Include(s => s.SourceFile)
            .FirstOrDefaultAsync(s => s.Id == subroutineId, ct);
        if (sub?.SourceFile is null) return null;

        var corpusId = sub.SourceFile.SourceVersion?.CorpusId
                       ?? (await _db.SourceVersions.AsNoTracking()
                               .Where(v => v.Id == sub.SourceFile.SourceVersionId)
                               .Select(v => v.CorpusId)
                               .FirstOrDefaultAsync(ct));
        if (corpusId == Guid.Empty) return null;

        var plan = await _db.MigrationPlans.AsNoTracking()
            .Where(p => p.CorpusId == corpusId && p.Status != "archived")
            .OrderByDescending(p => p.Status == "approved")
            .ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (plan is null) return null;

        var waves = await _db.MigrationWaves.AsNoTracking()
            .Where(w => w.MigrationPlanId == plan.Id)
            .OrderBy(w => w.WaveNumber)
            .ToListAsync(ct);

        var subIdString = subroutineId.ToString();
        foreach (var w in waves)
        {
            try
            {
                using var doc = JsonDocument.Parse(w.PlannedRoutineIdsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String
                        && string.Equals(el.GetString(), subIdString, StringComparison.OrdinalIgnoreCase))
                    {
                        return new WaveAssignment(
                            PlanId: plan.Id,
                            PlanStatus: plan.Status,
                            StrategyName: plan.StrategyName,
                            WaveNumber: w.WaveNumber,
                            TotalWaves: plan.TotalWaves,
                            WaveName: w.Name);
                    }
                }
            }
            catch { /* malformed JSON — skip wave */ }
        }
        return null;
    }

    private async Task<(Subroutine?, DependencyGraph?)> LoadGraphForSubroutineAsync(
        Guid subroutineId, CancellationToken ct)
    {
        var sub = await _db.Subroutines.AsNoTracking()
            .Include(s => s.SourceFile)
                .ThenInclude(f => f!.SourceVersion)
            .FirstOrDefaultAsync(s => s.Id == subroutineId, ct);
        if (sub?.SourceFile?.SourceVersion is null)
        {
            _log.LogWarning("Migration context requested for unknown subroutine {Id}", subroutineId);
            return (null, null);
        }
        var corpusId = sub.SourceFile.SourceVersion.CorpusId;
        var graph = await _graphBuilder.BuildForVersionAsync(corpusId, sub.SourceFile.SourceVersionId, ct);
        return (sub, graph);
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

// ───────────────────────────────────────────────────────────────────
// Records returned by the service
// ───────────────────────────────────────────────────────────────────

public sealed record BlastRadius(
    Guid SubroutineId,
    string SubroutineName,
    int DirectCallerCount,
    int TransitiveCallerCount,
    int SharedStorageConsumerCount,
    IReadOnlyList<BlastRadiusEntry> Affected,
    StateBreakdown StateBreakdown);

public sealed record BlastRadiusEntry(
    Guid Id,
    string Name,
    string State,
    /// <summary>"direct-caller" | "transitive-caller" |
    /// "shared-storage" | "direct-caller+shared-storage".</summary>
    string Reason,
    IReadOnlyList<string> SharedBlocks);

public sealed record StateBreakdown(int Signed, int Scaffolded, int Draft, int Parsed);

public sealed record MigrationReadiness(
    Guid SubroutineId,
    string SubroutineName,
    /// <summary>"safe-leaf" | "safe-with-deps-done" | "blocked-on-deps"
    /// | "coordinated-only".</summary>
    string Classification,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<Guid> BlockingRoutineIds,
    int CalleeCount,
    int CallerCount,
    IReadOnlyList<string> SharedBlockNames);

public sealed record WaveAssignment(
    Guid PlanId,
    string PlanStatus,
    string StrategyName,
    int WaveNumber,
    int TotalWaves,
    string WaveName);
