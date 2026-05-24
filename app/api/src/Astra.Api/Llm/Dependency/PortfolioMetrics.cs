using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm.Dependency;

/// <summary>
/// Phase 8.0.d — Cross-corpus aggregates for the program-manager view.
///
/// Computes:
///   - Platform-wide totals: routines, signed, scaffolded, committed
///   - LLM cost rollup: total tokens + USD spend
///   - Per-corpus rollup: latest plan status, % signed, % scaffolded,
///     % committed, last-activity timestamp
///   - Recent activity feed (last N audit events)
///
/// Pure read-only aggregation over existing tables — no schema
/// additions. Single query per dimension; in-memory join in C#.
/// Suitable for corpora up to ~10k routines; beyond that we'd want
/// materialised views.
/// </summary>
public sealed class PortfolioMetricsService
{
    public const int RecentActivityLimit = 30;

    private readonly AppDbContext _db;
    private readonly ILogger<PortfolioMetricsService> _log;

    public PortfolioMetricsService(AppDbContext db, ILogger<PortfolioMetricsService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<PortfolioSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var corpora = await _db.Corpora.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        // Pull latest-version-id per corpus.
        var latestVersionIds = corpora
            .Where(c => c.LatestVersionId.HasValue)
            .Select(c => c.LatestVersionId!.Value)
            .ToList();

        // Subroutines in the latest version per corpus.
        var subs = await _db.Subroutines.AsNoTracking()
            .Include(s => s.SourceFile)
            .Where(s => s.SourceFile != null
                     && latestVersionIds.Contains(s.SourceFile.SourceVersionId))
            .Select(s => new SubProj
            {
                Id = s.Id,
                State = s.State,
                SourceVersionId = s.SourceFile!.SourceVersionId,
            })
            .ToListAsync(ct);

        // Latest spec per subroutine.
        var subIds = subs.Select(s => s.Id).ToList();
        var specsRaw = await _db.Specs.AsNoTracking()
            .Where(sp => subIds.Contains(sp.SubroutineId))
            .OrderByDescending(sp => sp.UpdatedAt)
            .Select(sp => new SpecProj
            {
                Id = sp.Id,
                SubroutineId = sp.SubroutineId,
                State = sp.State,
                UpdatedAt = sp.UpdatedAt,
            })
            .ToListAsync(ct);
        var latestSpecBySub = specsRaw
            .GroupBy(sp => sp.SubroutineId)
            .ToDictionary(g => g.Key, g => g.First());

        // Latest scaffold per spec.
        var specIds = specsRaw.Select(sp => sp.Id).ToList();
        var scaffoldsRaw = specIds.Count == 0
            ? new List<ScaffoldProj>()
            : await _db.Scaffolds.AsNoTracking()
                .Where(sc => specIds.Contains(sc.SpecId))
                .OrderByDescending(sc => sc.GeneratedAt)
                .Select(sc => new ScaffoldProj { Id = sc.Id, SpecId = sc.SpecId, State = sc.State, GeneratedAt = sc.GeneratedAt })
                .ToListAsync(ct);
        var latestScaffoldBySpec = scaffoldsRaw
            .GroupBy(sc => sc.SpecId)
            .ToDictionary(g => g.Key, g => g.First());

        // Map version → corpus for the per-corpus rollup.
        var corpusIdByVersion = corpora
            .Where(c => c.LatestVersionId.HasValue)
            .ToDictionary(c => c.LatestVersionId!.Value, c => c.Id);

        // Map subroutine → state.
        int totalRoutines = 0, signed = 0, scaffolded = 0, committed = 0, draft = 0, parsed = 0;
        var perCorpusCounts = new Dictionary<Guid, RollupCounts>();
        foreach (var c in corpora) perCorpusCounts[c.Id] = new RollupCounts();
        foreach (var sub in subs)
        {
            totalRoutines++;
            if (!corpusIdByVersion.TryGetValue(sub.SourceVersionId, out var cid)) continue;
            var bucket = perCorpusCounts[cid];
            bucket.Total++;
            Spec? spec = null;
            if (latestSpecBySub.TryGetValue(sub.Id, out var sp))
            {
                spec = new Spec { Id = sp.Id, SubroutineId = sp.SubroutineId, State = sp.State };
            }
            Scaffold? scaffold = null;
            if (spec is not null && latestScaffoldBySpec.TryGetValue(spec.Id, out var sc))
            {
                scaffold = new Scaffold { Id = sc.Id, SpecId = sc.SpecId, State = sc.State };
            }
            var state = ClassifyState(sub.State, spec?.State, scaffold?.State);
            switch (state)
            {
                case "COMMITTED": committed++; scaffolded++; signed++; bucket.Committed++; bucket.Scaffolded++; bucket.Signed++; break;
                case "SCAFFOLDED": scaffolded++; signed++; bucket.Scaffolded++; bucket.Signed++; break;
                case "SIGNED": signed++; bucket.Signed++; break;
                case "DRAFT":
                case "IN_REVIEW": draft++; bucket.Draft++; break;
                default: parsed++; bucket.Parsed++; break;
            }
        }

        // Latest migration plan per corpus (approved else most-recent
        // draft) for the per-corpus row.
        var plansRaw = await _db.MigrationPlans.AsNoTracking()
            .Where(p => p.Status != "archived")
            .OrderByDescending(p => p.Status == "approved")
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        var planByCorpus = plansRaw
            .GroupBy(p => p.CorpusId)
            .ToDictionary(g => g.Key, g => g.First());

        // LLM cost rollup.
        var llmAgg = await _db.LlmCalls.AsNoTracking()
            .GroupBy(l => 1)
            .Select(g => new
            {
                Calls = g.Count(),
                InputTokens = g.Sum(l => (long)l.InputTokens),
                OutputTokens = g.Sum(l => (long)l.OutputTokens),
                CostUsd = g.Sum(l => l.CostUsd),
            })
            .FirstOrDefaultAsync(ct);
        var llmTotals = llmAgg is null
            ? new LlmTotals(0, 0, 0, 0m)
            : new LlmTotals(llmAgg.Calls, llmAgg.InputTokens, llmAgg.OutputTokens, llmAgg.CostUsd);

        // Per-corpus rollup objects.
        var perCorpus = corpora.Select(c =>
        {
            var counts = perCorpusCounts.GetValueOrDefault(c.Id) ?? new RollupCounts();
            MigrationPlan? plan = planByCorpus.GetValueOrDefault(c.Id);
            return new PortfolioCorpusRow(
                CorpusId: c.Id,
                Name: c.Name,
                State: c.State,
                FileCount: c.FileCount,
                TotalLoc: c.TotalLoc,
                LastActivity: c.UpdatedAt,
                Counts: counts,
                PlanStatus: plan?.Status,
                PlanId: plan?.Id,
                TotalWaves: plan?.TotalWaves);
        }).ToList();

        // Recent activity feed — last 30 audit events across the platform.
        // EF can't translate named-argument record constructors inside a
        // Select; project to anonymous + build records in-memory.
        var recentRaw = await _db.AuditEvents.AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(RecentActivityLimit)
            .Select(e => new
            {
                e.EventType,
                e.ActorDisplay,
                e.ActorPersona,
                e.TargetType,
                e.TargetId,
                e.OccurredAt,
            })
            .ToListAsync(ct);
        var recent = recentRaw
            .Select(e => new ActivityEntry(
                e.EventType, e.ActorDisplay, e.ActorPersona, e.TargetType, e.TargetId, e.OccurredAt))
            .ToList();

        var summary = new PortfolioSummary(
            Totals: new PortfolioTotals(
                CorpusCount: corpora.Count,
                TotalRoutines: totalRoutines,
                SignedCount: signed,
                ScaffoldedCount: scaffolded,
                CommittedCount: committed,
                DraftCount: draft,
                ParsedCount: parsed),
            LlmTotals: llmTotals,
            Corpora: perCorpus,
            Recent: recent);

        _log.LogInformation(
            "Portfolio summary: {Corpora} corpora, {Routines} routines, {Signed} signed, {Committed} committed; LLM cost ${Cost}",
            corpora.Count, totalRoutines, signed, committed, llmTotals.CostUsd);
        return summary;
    }

    private static string ClassifyState(string subState, string? specState, string? scaffoldState)
    {
        if (scaffoldState == "COMMITTED") return "COMMITTED";
        if (scaffoldState is not null) return "SCAFFOLDED";
        if (specState == "SIGNED") return "SIGNED";
        if (specState is not null) return specState;
        return subState;
    }

    private sealed class SubProj
    {
        public Guid Id;
        public string State = "";
        public Guid SourceVersionId;
    }

    private sealed class SpecProj
    {
        public Guid Id;
        public Guid SubroutineId;
        public string State = "";
        public DateTimeOffset UpdatedAt;
    }

    private sealed class ScaffoldProj
    {
        public Guid Id;
        public Guid SpecId;
        public string State = "";
        public DateTimeOffset GeneratedAt;
    }
}

public sealed record PortfolioSummary(
    PortfolioTotals Totals,
    LlmTotals LlmTotals,
    IReadOnlyList<PortfolioCorpusRow> Corpora,
    IReadOnlyList<ActivityEntry> Recent);

public sealed record PortfolioTotals(
    int CorpusCount,
    int TotalRoutines,
    int SignedCount,
    int ScaffoldedCount,
    int CommittedCount,
    int DraftCount,
    int ParsedCount);

public sealed record LlmTotals(
    int CallCount,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

public sealed record PortfolioCorpusRow(
    Guid CorpusId,
    string Name,
    string State,
    int FileCount,
    int TotalLoc,
    DateTimeOffset LastActivity,
    RollupCounts Counts,
    string? PlanStatus,
    Guid? PlanId,
    int? TotalWaves);

public sealed class RollupCounts
{
    public int Total { get; set; }
    public int Signed { get; set; }
    public int Scaffolded { get; set; }
    public int Committed { get; set; }
    public int Draft { get; set; }
    public int Parsed { get; set; }
}

public sealed record ActivityEntry(
    string EventType,
    string ActorDisplay,
    string ActorPersona,
    string TargetType,
    Guid? TargetId,
    DateTimeOffset OccurredAt);
