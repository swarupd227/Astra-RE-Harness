using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Astra.Api.Work;

/// <summary>Model usage of one execution of an item.</summary>
public sealed record WorkUsage(int Calls, long InputTokens, long OutputTokens, decimal CostUsd)
{
    public static readonly WorkUsage None = new(0, 0, 0, 0m);
}

/// <summary>
/// The durable queue over <c>work_runs</c> / <c>work_items</c>. Leasing uses
/// <c>FOR UPDATE SKIP LOCKED</c> under a per-kind advisory lock (so concurrency
/// caps hold with more than one API instance); every write that finishes an
/// item is fenced on the lease owner, so a worker whose lease lapsed cannot
/// overwrite the result of whoever took the item over.
/// </summary>
public sealed class WorkQueue
{
    private readonly AppDbContext _db;
    public WorkQueue(AppDbContext db) => _db = db;

    public async Task CreateRunAsync(WorkRun run, IReadOnlyList<WorkItem> items, CancellationToken ct)
    {
        _db.WorkRuns.Add(run);
        _db.WorkItems.AddRange(items);
        await _db.SaveChangesAsync(ct);
    }

    // ── leasing ──────────────────────────────────────────────────────────

    private const string LeaseSql = """
        WITH picked AS (
            SELECT w.id
              FROM work_items w
              JOIN work_runs r ON r.id = w.run_id
             WHERE w.state = 'PENDING'
               AND w.kind = @kind
               AND r.state = 'RUNNING'
               AND (w.not_before IS NULL OR w.not_before <= now())
               AND (SELECT count(*) FROM work_items x WHERE x.kind = @kind AND x.state = 'RUNNING') < @cap
               AND (r.caps_json ->> w.kind IS NULL
                    OR (SELECT count(*) FROM work_items y
                         WHERE y.run_id = w.run_id AND y.kind = w.kind AND y.state = 'RUNNING')
                       < (r.caps_json ->> w.kind)::int)
               AND NOT EXISTS (SELECT 1 FROM work_items d WHERE d.id = ANY(w.depends_on) AND d.state <> 'SUCCEEDED')
             ORDER BY w.priority, w.created_at, w.subject_name
             LIMIT 1
             FOR UPDATE OF w SKIP LOCKED
        )
        UPDATE work_items u
           SET state = 'RUNNING',
               lease_owner = @owner,
               lease_expires_at = now() + make_interval(secs => @lease),
               heartbeat_at = now(),
               attempts = u.attempts + 1,
               started_at = now(),
               completed_at = NULL,
               error = NULL
          FROM picked
         WHERE u.id = picked.id
        RETURNING u.id
        """;

    /// <summary>Lease the next ready item of a kind, or null when there is none (or the cap is reached).</summary>
    public async Task<Guid?> LeaseAsync(string kind, string owner, int leaseSeconds, int cap, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // Serialises leasing per kind across instances, so the running count read below is exact.
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", new object[] { "astra.work." + kind }, ct);
        var ids = await QueryGuidsAsync(LeaseSql, ct,
            new NpgsqlParameter("kind", kind),
            new NpgsqlParameter("owner", owner),
            new NpgsqlParameter("lease", (double)leaseSeconds),
            new NpgsqlParameter("cap", (long)cap));
        await tx.CommitAsync(ct);
        return ids.Count == 0 ? null : ids[0];
    }

    public async Task HeartbeatAsync(IReadOnlyCollection<Guid> ids, string owner, int leaseSeconds, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await _db.Database.ExecuteSqlRawAsync("""
            UPDATE work_items
               SET heartbeat_at = now(), lease_expires_at = now() + make_interval(secs => {2})
             WHERE id = ANY({0}) AND lease_owner = {1} AND state = 'RUNNING'
            """, new object[] { ids.ToArray(), owner, (double)leaseSeconds }, ct);
    }

    /// <summary>Items whose lease lapsed (the process running them is gone) go back to
    /// PENDING, or FAILED once they have used every attempt. Returns the runs touched.</summary>
    public Task<List<Guid>> ReapExpiredAsync(CancellationToken ct) => QueryGuidsAsync("""
        UPDATE work_items w
           SET state = CASE WHEN r.state = 'CANCELLED' THEN 'CANCELLED'
                            WHEN w.attempts >= w.max_attempts THEN 'FAILED'
                            ELSE 'PENDING' END,
               completed_at = CASE WHEN r.state = 'CANCELLED' OR w.attempts >= w.max_attempts THEN now() ELSE NULL END,
               error = 'Interrupted: the API restarted or the worker stopped before this step finished.',
               lease_owner = NULL, lease_expires_at = NULL
          FROM work_runs r
         WHERE r.id = w.run_id AND w.state = 'RUNNING' AND w.lease_expires_at < now()
        RETURNING w.run_id
        """, ct);

    // ── the human wait state ─────────────────────────────────────────────

    /// <summary>Observe sign-offs: a waiting signoff item succeeds when its routine's spec is
    /// signed. This is the only way one completes — nothing here signs anything.</summary>
    public Task<List<Guid>> PromoteSignoffsAsync(CancellationToken ct) => QueryGuidsAsync("""
        UPDATE work_items w
           SET state = 'SUCCEEDED', started_at = COALESCE(w.started_at, now()), completed_at = now(), error = NULL
          FROM subroutines s, work_runs r
         WHERE w.kind = 'signoff' AND w.state = 'WAITING'
           AND r.id = w.run_id AND r.state IN ('RUNNING', 'PAUSED')
           AND s.id = w.subject_id
           AND s.state IN ('SIGNED', 'SCAFFOLDING', 'SCAFFOLDED', 'COMMITTED')
           AND NOT EXISTS (SELECT 1 FROM work_items d WHERE d.id = ANY(w.depends_on) AND d.state <> 'SUCCEEDED')
        RETURNING w.run_id
        """, ct);

    /// <summary>Drafts a run is waiting on that nobody has routed to review yet.</summary>
    public async Task<List<(Guid RunId, Guid SubroutineId, string Persona, string DisplayName)>> DraftsToRouteAsync(CancellationToken ct)
    {
        var rows = await (
            from w in _db.WorkItems.AsNoTracking()
            join r in _db.WorkRuns.AsNoTracking() on w.RunId equals r.Id
            join s in _db.Subroutines.AsNoTracking() on w.SubjectId equals s.Id
            where w.Kind == WorkStages.Signoff && w.State == "WAITING" && r.State == "RUNNING" && s.State == "DRAFT"
                  && !_db.WorkItems.Any(d => w.DependsOn.Contains(d.Id) && d.State != "SUCCEEDED")
            select new { r.Id, w.SubjectId, r.Persona, r.DisplayName }
        ).Take(50).ToListAsync(ct);
        return rows.Select(x => (x.Id, x.SubjectId, x.Persona, x.DisplayName)).ToList();
    }

    /// <summary>Steps behind a failed, skipped or cancelled step can never run: mark them SKIPPED.
    /// Repeats until the whole chain is marked.</summary>
    public async Task<List<Guid>> SkipBlockedAsync(CancellationToken ct)
    {
        var touched = new List<Guid>();
        while (true)
        {
            var ids = await QueryGuidsAsync("""
                UPDATE work_items w
                   SET state = 'SKIPPED', completed_at = now(),
                       error = 'Skipped: an earlier step for this routine did not succeed.'
                 WHERE w.state IN ('PENDING', 'WAITING')
                   AND EXISTS (SELECT 1 FROM work_items d
                                WHERE d.id = ANY(w.depends_on) AND d.state IN ('FAILED', 'SKIPPED', 'CANCELLED'))
                RETURNING w.run_id
                """, ct);
            if (ids.Count == 0) return touched;
            touched.AddRange(ids);
        }
    }

    // ── finishing items ──────────────────────────────────────────────────

    /// <summary>Close an item this owner holds. Usage is added to the run either way (the
    /// money was spent); the state only changes while the lease is still ours.</summary>
    public async Task<bool> FinishAsync(Guid itemId, Guid runId, string owner, string state, string? error, string? resultJson, WorkUsage usage, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var trimmed = error is { Length: > 4000 } ? error[..4000] : error;
        var changed = await _db.WorkItems
            .Where(w => w.Id == itemId && w.LeaseOwner == owner && w.State == "RUNNING")
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.State, state)
                .SetProperty(w => w.Error, trimmed)
                .SetProperty(w => w.ResultJson, resultJson)
                .SetProperty(w => w.CompletedAt, now)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(w => w.CostUsd, w => w.CostUsd + usage.CostUsd)
                .SetProperty(w => w.Calls, w => w.Calls + usage.Calls)
                .SetProperty(w => w.InputTokens, w => w.InputTokens + usage.InputTokens)
                .SetProperty(w => w.OutputTokens, w => w.OutputTokens + usage.OutputTokens), ct);
        await AddRunUsageAsync(runId, usage, ct);
        return changed == 1;
    }

    /// <summary>Put an item this owner holds back in the queue.</summary>
    public async Task RequeueAsync(Guid itemId, Guid runId, string owner, string error, TimeSpan delay, bool refundAttempt, WorkUsage usage, CancellationToken ct)
    {
        var notBefore = DateTimeOffset.UtcNow + delay;
        var trimmed = error.Length > 4000 ? error[..4000] : error;
        var refund = refundAttempt ? 1 : 0;
        await _db.WorkItems
            .Where(w => w.Id == itemId && w.LeaseOwner == owner && w.State == "RUNNING")
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.State, "PENDING")
                .SetProperty(w => w.Error, trimmed)
                .SetProperty(w => w.NotBefore, notBefore)
                .SetProperty(w => w.Attempts, w => w.Attempts - refund)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(w => w.CostUsd, w => w.CostUsd + usage.CostUsd)
                .SetProperty(w => w.Calls, w => w.Calls + usage.Calls)
                .SetProperty(w => w.InputTokens, w => w.InputTokens + usage.InputTokens)
                .SetProperty(w => w.OutputTokens, w => w.OutputTokens + usage.OutputTokens), ct);
        await AddRunUsageAsync(runId, usage, ct);
    }

    private async Task AddRunUsageAsync(Guid runId, WorkUsage usage, CancellationToken ct)
    {
        if (usage == WorkUsage.None) return;
        await _db.WorkRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.CostUsd, r => r.CostUsd + usage.CostUsd)
            .SetProperty(r => r.Calls, r => r.Calls + usage.Calls)
            .SetProperty(r => r.InputTokens, r => r.InputTokens + usage.InputTokens)
            .SetProperty(r => r.OutputTokens, r => r.OutputTokens + usage.OutputTokens), ct);
    }

    // ── run control ──────────────────────────────────────────────────────

    public async Task<bool> PauseAsync(Guid runId, string reason, CancellationToken ct) =>
        await _db.WorkRuns.Where(r => r.Id == runId && r.State == "RUNNING")
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "PAUSED").SetProperty(r => r.Summary, reason), ct) == 1;

    public async Task<bool> ResumeAsync(Guid runId, decimal? newBudgetUsd, CancellationToken ct, string? newCapsJson = null)
    {
        if (newBudgetUsd is { } budget)
            await _db.WorkRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(r => r.BudgetUsd, budget), ct);
        if (newCapsJson is not null)
            await _db.WorkRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(r => r.CapsJson, newCapsJson), ct);
        return await _db.WorkRuns.Where(r => r.Id == runId && r.State == "PAUSED")
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "RUNNING").SetProperty(r => r.Summary, ""), ct) == 1;
    }

    /// <summary>Cancel: nothing new starts; waiting and queued steps are closed. Steps already
    /// running are told to stop by the scheduler. Finished work is kept.</summary>
    public async Task<bool> CancelAsync(Guid runId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await _db.WorkRuns.Where(r => r.Id == runId && (r.State == "RUNNING" || r.State == "PAUSED"))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "CANCELLED").SetProperty(r => r.CompletedAt, now), ct);
        if (changed != 1) return false;
        await _db.WorkItems.Where(w => w.RunId == runId && (w.State == "PENDING" || w.State == "WAITING"))
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.State, "CANCELLED").SetProperty(w => w.CompletedAt, now), ct);
        return true;
    }

    /// <summary>Re-queue every failed step and the steps skipped behind them; reopens a completed run.</summary>
    public async Task<int> RetryFailedAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.WorkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.State == "CANCELLED") return 0;
        var failed = await _db.WorkItems.CountAsync(w => w.RunId == runId && w.State == "FAILED", ct);
        if (failed == 0) return 0;
        await _db.WorkItems.Where(w => w.RunId == runId && (w.State == "FAILED" || w.State == "SKIPPED"))
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.State, w => w.Kind == WorkStages.Signoff ? "WAITING" : "PENDING")
                .SetProperty(w => w.Attempts, 0)
                .SetProperty(w => w.Error, (string?)null)
                .SetProperty(w => w.NotBefore, (DateTimeOffset?)null)
                .SetProperty(w => w.StartedAt, (DateTimeOffset?)null)
                .SetProperty(w => w.CompletedAt, (DateTimeOffset?)null), ct);
        await _db.WorkRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.State, "RUNNING")
                .SetProperty(r => r.Summary, "")
                .SetProperty(r => r.MetricsJson, (string?)null)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)null), ct);
        return failed;
    }

    /// <summary>Complete a running run whose every step is closed, recording serial against
    /// parallel time. True when this call completed it.</summary>
    public async Task<bool> TryFinishRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.WorkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.State is not ("RUNNING" or "PAUSED")) return false;
        var open = await _db.WorkItems.AnyAsync(w => w.RunId == runId && (w.State == "PENDING" || w.State == "RUNNING" || w.State == "WAITING"), ct);
        if (open) return false;

        var now = DateTimeOffset.UtcNow;
        var items = await _db.WorkItems.AsNoTracking().Where(w => w.RunId == runId).ToListAsync(ct);
        run.CompletedAt = now;
        var metrics = WorkRunText.Metrics(run, items, now);
        var counts = Count(items, Caps(run.CapsJson));
        var summary = WorkRunText.Progress(counts, 0, items.Count(i => i.State == "FAILED"), run.CostUsd);
        var metricsJson = JsonSerializer.Serialize(metrics, Json);
        // A wave whose whole pipeline went through for every routine is done.
        if (run.MigrationWaveId is { } waveId
            && items.All(i => i.State == "SUCCEEDED")
            && items.Any(i => i.Kind == WorkStages.TestPack))
        {
            await _db.MigrationWaves.Where(w => w.Id == waveId)
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, "completed").SetProperty(w => w.ActualCompletedAt, now), ct);
        }
        return await _db.WorkRuns.Where(r => r.Id == runId && (r.State == "RUNNING" || r.State == "PAUSED"))
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.State, "COMPLETED")
                .SetProperty(r => r.CompletedAt, now)
                .SetProperty(r => r.MetricsJson, metricsJson)
                .SetProperty(r => r.Summary, summary.Length > 1024 ? summary.Substring(0, 1024) : summary), ct) == 1;
    }

    // ── reading ──────────────────────────────────────────────────────────

    public async Task<WorkRunSnapshot?> SnapshotAsync(Guid runId, CancellationToken ct)
    {
        var run = await _db.WorkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return null;
        var items = await _db.WorkItems.AsNoTracking().Where(w => w.RunId == runId).ToListAsync(ct);
        return Snapshot(run, items, DateTimeOffset.UtcNow);
    }

    /// <summary>Pure: a run and its items → what every surface shows.</summary>
    public static WorkRunSnapshot Snapshot(WorkRun run, IReadOnlyList<WorkItem> items, DateTimeOffset now)
    {
        var done = items.Where(i => i.State == "SUCCEEDED").Select(i => i.Id).ToHashSet();
        bool Ready(WorkItem i) => i.DependsOn.All(done.Contains);

        // A signoff item is "waiting for a person" only once the draft it waits for exists.
        var waiting = items.Where(i => i.Kind == WorkStages.Signoff && i.State == "WAITING" && Ready(i))
            .OrderBy(i => i.Priority).ThenBy(i => i.SubjectName, StringComparer.Ordinal).ToList();
        var failures = items.Where(i => i.State == "FAILED").OrderBy(i => i.CompletedAt).ToList();
        var running = items.Where(i => i.State == "RUNNING").OrderBy(i => i.StartedAt).ToList();
        var counts = Count(items, Caps(run.CapsJson));
        static WorkItemBrief Brief(WorkItem i) => new(i.Id, i.Kind, i.SubjectId, i.SubjectName, i.State, i.Error);

        return new WorkRunSnapshot(
            run.Id, run.Kind, run.Label, run.State, run.CorpusId, run.ConversationId,
            run.TargetStack, run.BudgetUsd, run.CostUsd, run.Calls, run.InputTokens, run.OutputTokens,
            items.Select(i => i.SubjectId).Distinct().Count(), counts,
            running.Take(12).Select(Brief).ToList(),
            waiting.Take(25).Select(Brief).ToList(),
            failures.Take(12).Select(Brief).ToList(),
            waiting.Count, failures.Count,
            run.State == "PAUSED" && run.Summary.Length > 0
                ? run.Summary
                : WorkRunText.Progress(counts, waiting.Count, failures.Count, run.CostUsd),
            WorkRunText.Metrics(run, items, now),
            run.CreatedAt, run.CompletedAt)
        {
            MachineReady = run.State == "RUNNING"
                ? items.Count(i => i.State == "PENDING" && !WorkStages.IsHuman(i.Kind) && Ready(i))
                : 0,
        };
    }

    public static IReadOnlyList<StageCounts> Count(IReadOnlyList<WorkItem> items, IReadOnlyDictionary<string, int> caps) =>
        items.GroupBy(i => i.Kind)
            .OrderBy(g => WorkStages.IndexOf(g.Key))
            .Select(g => new StageCounts(
                g.Key, g.Count(),
                g.Count(i => i.State == "PENDING"),
                g.Count(i => i.State == "RUNNING"),
                g.Count(i => i.State == "SUCCEEDED"),
                g.Count(i => i.State == "FAILED"),
                g.Count(i => i.State == "SKIPPED"),
                g.Count(i => i.State == "WAITING"),
                g.Count(i => i.State == "CANCELLED"),
                caps.TryGetValue(g.Key, out var cap) ? cap : 0))
            .ToList();

    public static IReadOnlyDictionary<string, int> Caps(string capsJson)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(capsJson) ?? new(); }
        catch (JsonException) { return new Dictionary<string, int>(); }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<List<Guid>> QueryGuidsAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = (NpgsqlCommand)_db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = sql;
            if (_db.Database.CurrentTransaction is { } tx) cmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
            cmd.Parameters.AddRange(parameters);
            var ids = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            return ids;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
