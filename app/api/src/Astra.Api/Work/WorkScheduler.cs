using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Copilot;
using Astra.Api.Endpoints;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Astra.Api.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Work;

public sealed class WorkSchedulerOptions
{
    /// <summary>Whether this instance executes queued items (queueing works either way).</summary>
    public bool Enabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 750;
    /// <summary>How long a leased item survives without a heartbeat before another worker may take it.</summary>
    public int LeaseSeconds { get; set; } = 90;
    /// <summary>Back-off before a retryable failure is tried again (multiplied by the attempt).</summary>
    public int RetryDelaySeconds { get; set; } = 20;
    public int StartupDelaySeconds { get; set; } = 5;
    /// <summary>Process-wide ceiling per stage across all runs; defaults to twice the per-run default.</summary>
    public Dictionary<string, int> Caps { get; set; } = new();

    public int CapFor(string kind) =>
        Caps.TryGetValue(kind, out var c) && c > 0 ? c
        : WorkStages.DefaultCaps.TryGetValue(kind, out var d) ? d * 2 : 1;
}

/// <summary>
/// P5 — the hosted scheduler over the durable queue. Each pass: reclaim lapsed
/// leases, observe human sign-offs, lease ready items up to the stage caps and
/// run them, heartbeat what is in flight, close finished runs and publish each
/// changed run's snapshot on the <see cref="RunEventBus"/> for its Narrator.
/// Everything it knows is in Postgres, so a restarted API simply carries on
/// (the generalisation of the pattern-analysis resume service).
/// </summary>
public sealed class WorkScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly WorkStepExecutor _executor;
    private readonly RunEventBus _bus;
    private readonly Narrator _narrator;
    private readonly WorkSchedulerOptions _opts;
    private readonly ILogger<WorkScheduler> _logger;

    private readonly ConcurrentDictionary<Guid, Local> _local = new();
    private readonly ConcurrentDictionary<Guid, byte> _dirty = new();
    private readonly ConcurrentDictionary<Guid, string> _published = new();
    private readonly SemaphoreSlim _wake = new(0);

    public string InstanceId { get; } = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";

    private sealed record Local(Guid RunId, string Kind, CancellationTokenSource Cancel, Task Task);

    public WorkScheduler(
        IServiceScopeFactory scopes, WorkStepExecutor executor, RunEventBus bus, Narrator narrator,
        IOptions<WorkSchedulerOptions> opts, ILogger<WorkScheduler> logger)
    {
        _scopes = scopes;
        _executor = executor;
        _bus = bus;
        _narrator = narrator;
        _opts = opts.Value;
        _logger = logger;
    }

    /// <summary>A run changed (created, paused, resumed …): publish it and look for work now.</summary>
    public void Poke(Guid runId)
    {
        _dirty[runId] = 1;
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled) return;
        try
        {
            // Let the schema bootstrap, seeds and provider wiring settle first.
            await Task.Delay(TimeSpan.FromSeconds(_opts.StartupDelaySeconds), stoppingToken);
            var rehydrated = false;
            var lastHeartbeat = DateTimeOffset.MinValue;
            var heartbeatEvery = TimeSpan.FromSeconds(Math.Max(1, _opts.LeaseSeconds / 3));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!rehydrated)
                    {
                        await RehydrateAsync(stoppingToken);
                        rehydrated = true;
                    }
                    if (DateTimeOffset.UtcNow - lastHeartbeat >= heartbeatEvery)
                    {
                        using var scope = _scopes.CreateScope();
                        await Queue(scope).HeartbeatAsync(_local.Keys.ToArray(), InstanceId, _opts.LeaseSeconds, stoppingToken);
                        lastHeartbeat = DateTimeOffset.UtcNow;
                    }
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Work scheduler pass failed; trying again");
                }
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(_opts.PollMilliseconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Graceful stop: the running items hand their leases back (see ExecuteItemAsync)
            // so the next process picks them up at once instead of waiting for expiry.
            var inFlight = _local.Values.Select(l => l.Task).ToArray();
            if (inFlight.Length > 0)
                await Task.WhenAny(Task.WhenAll(inFlight), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
    }

    /// <summary>After boot: follow every unfinished run again (the bus and the Narrator are in memory).</summary>
    private async Task RehydrateAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var live = await db.WorkRuns.AsNoTracking().Where(r => r.State == "RUNNING" || r.State == "PAUSED").ToListAsync(ct);
        foreach (var run in live)
        {
            Follow(run);
            _dirty[run.Id] = 1;
        }
        if (live.Count > 0) _logger.LogInformation("Work scheduler: resuming {Count} unfinished run(s) from the queue", live.Count);
    }

    /// <summary>Have the Narrator follow a run into its thread.</summary>
    public void Follow(WorkRun run)
    {
        if (run.ConversationId is not { } conversation) return;
        _narrator.Track(run.Id, conversation, "programme", "work-run", run.Label, run.CorpusId,
            run.Persona.ToLowerInvariant(), run.DisplayName);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var queue = Queue(scope);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var id in await queue.ReapExpiredAsync(ct)) _dirty[id] = 1;
        foreach (var id in await queue.PromoteSignoffsAsync(ct)) _dirty[id] = 1;
        await RouteDraftsAsync(queue, ct);
        foreach (var id in await queue.SkipBlockedAsync(ct)) _dirty[id] = 1;

        // Cancelled runs: tell their in-flight items to stop.
        var localRuns = _local.Values.Select(l => l.RunId).Distinct().ToArray();
        if (localRuns.Length > 0)
        {
            var cancelled = await db.WorkRuns.AsNoTracking()
                .Where(r => localRuns.Contains(r.Id) && r.State == "CANCELLED").Select(r => r.Id).ToListAsync(ct);
            foreach (var l in _local.Values.Where(l => cancelled.Contains(l.RunId)))
                l.Cancel.Cancel();
        }

        foreach (var kind in WorkStages.Machine)
        {
            var cap = _opts.CapFor(kind);
            while (_local.Values.Count(l => l.Kind == kind) < cap)
            {
                var id = await queue.LeaseAsync(kind, InstanceId, _opts.LeaseSeconds, cap, ct);
                if (id is not { } itemId) break;
                var item = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == itemId, ct);
                var run = await db.WorkRuns.AsNoTracking().FirstAsync(r => r.Id == item.RunId, ct);
                Start(run, item, ct);
                _dirty[run.Id] = 1;
            }
        }

        foreach (var runId in _dirty.Keys.ToArray())
        {
            _dirty.TryRemove(runId, out _);
            await queue.TryFinishRunAsync(runId, ct);
            await PublishAsync(queue, db, runId, ct);
        }
    }

    /// <summary>Send drafts a run is waiting on to review, as the engineer who asked for the run.
    /// Routing only: reviewing and signing stay with the SME.</summary>
    private async Task RouteDraftsAsync(WorkQueue queue, CancellationToken ct)
    {
        foreach (var draft in await queue.DraftsToRouteAsync(ct))
        {
            if (!string.Equals(draft.Persona, nameof(Persona.Engineer), StringComparison.OrdinalIgnoreCase)) continue;
            using var scope = _scopes.CreateScope();
            var actor = scope.ServiceProvider.GetRequiredService<DevPersonaContext>();
            actor.Persona = Persona.Engineer;
            actor.DisplayName = draft.DisplayName;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var specId = await db.Specs.AsNoTracking().Where(s => s.SubroutineId == draft.SubroutineId && s.State == "DRAFT")
                .OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (specId is not { } id) continue;
            var (_, error) = await scope.ServiceProvider.GetRequiredService<SpecReviewService>()
                .RouteAsync(id, new RouteRequest(null, "Routed for review by a work run."), actor, null, ct);
            if (error is not null) _logger.LogWarning("Could not route spec {Spec} for review: {Code} {Message}", id, error.Code, error.Message);
            _dirty[draft.RunId] = 1;
        }
    }

    private void Start(WorkRun run, WorkItem item, CancellationToken stopping)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await gate.Task; // registered in _local before it can finish and remove itself
            await ExecuteItemAsync(run, item, cancel.Token, stopping);
        }, CancellationToken.None);
        _local[item.Id] = new Local(run.Id, item.Kind, cancel, task);
        gate.SetResult();
    }

    private async Task ExecuteItemAsync(WorkRun run, WorkItem item, CancellationToken ct, CancellationToken stopping)
    {
        var budget = LlmWorkScope.BudgetFor(run.Id, run.BudgetUsd, run.CostUsd);
        var llm = LlmWorkScope.Begin(item.Lane == "interactive" ? LlmLane.Interactive : LlmLane.Batch, budget);
        StepOutcome outcome;
        try
        {
            outcome = await _executor.RunAsync(run, item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = StepOutcome.Fail("Stopped before it finished.");
        }
        catch (LlmBudgetExceededException ex)
        {
            outcome = StepOutcome.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Work item {Item} ({Kind} {Subject}) failed", item.Id, item.Kind, item.SubjectName);
            outcome = StepOutcome.Fail(BackgroundRunService.Reason(ex), retryable: true);
        }

        var usage = new WorkUsage(llm.Calls, llm.InputTokens, llm.OutputTokens, llm.CostUsd);
        try
        {
            using var scope = _scopes.CreateScope();
            var queue = Queue(scope);
            var none = CancellationToken.None;
            var succeeded = outcome.Verdict is StepVerdict.Succeeded or StepVerdict.Skipped;
            var result = outcome.Result is null ? null : JsonSerializer.Serialize(outcome.Result, Json);

            if (!succeeded && ct.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                // Only a cancelled run cancels its items: stopped on request is not a failure.
                await queue.FinishAsync(item.Id, run.Id, InstanceId, "CANCELLED", "Cancelled with the run before it finished.", result, usage, none);
            }
            else if (!succeeded && stopping.IsCancellationRequested)
            {
                await queue.RequeueAsync(item.Id, run.Id, InstanceId, "Interrupted by an API restart; queued again.", TimeSpan.Zero, refundAttempt: true, usage, none);
            }
            else if (!succeeded && budget.Exhausted)
            {
                await queue.RequeueAsync(item.Id, run.Id, InstanceId, "Waiting: the run's cost ceiling was reached.", TimeSpan.Zero, refundAttempt: true, usage, none);
            }
            else if (outcome.Verdict == StepVerdict.Busy)
            {
                await queue.RequeueAsync(item.Id, run.Id, InstanceId, outcome.Error ?? "Busy.",
                    TimeSpan.FromSeconds(_opts.RetryDelaySeconds), refundAttempt: false, usage, none);
            }
            else if (outcome.Verdict == StepVerdict.Failed && outcome.Retryable && item.Attempts < item.MaxAttempts)
            {
                await queue.RequeueAsync(item.Id, run.Id, InstanceId, outcome.Error ?? "Failed; will retry.",
                    TimeSpan.FromSeconds(_opts.RetryDelaySeconds * item.Attempts), refundAttempt: false, usage, none);
            }
            else
            {
                var state = outcome.Verdict switch
                {
                    StepVerdict.Succeeded => "SUCCEEDED",
                    StepVerdict.Skipped => "SKIPPED",
                    _ => "FAILED",
                };
                await queue.FinishAsync(item.Id, run.Id, InstanceId, state, outcome.Error, result, usage, none);
            }

            if (budget.Exhausted && budget.CeilingUsd is { } ceiling)
                await queue.PauseAsync(run.Id, $"Paused: the cost ceiling of ${ceiling:0.00} was reached (spent ${budget.SpentUsd:0.00}). Resume with a higher ceiling to carry on.", none);
        }
        catch (Exception ex)
        {
            // The lease will lapse and the item will be reclaimed.
            _logger.LogError(ex, "Could not record the result of work item {Item}", item.Id);
        }
        finally
        {
            if (_local.TryRemove(item.Id, out var local)) local.Cancel.Dispose();
            Poke(run.Id);
        }
    }

    /// <summary>Publish a run's snapshot when it changed; end the stream when the run is over.</summary>
    private async Task PublishAsync(WorkQueue queue, AppDbContext db, Guid runId, CancellationToken ct)
    {
        var snapshot = await queue.SnapshotAsync(runId, ct);
        if (snapshot is null) return;
        var signature = snapshot.State + "|" + snapshot.Summary + "|" + snapshot.MachineOpen + "|" +
                        string.Join(",", snapshot.Stages.Select(s => $"{s.Stage}:{s.Succeeded}/{s.Running}/{s.Failed}"));
        if (_published.TryGetValue(runId, out var last) && last == signature) return;
        _published[runId] = signature;

        // A cancelled run is not over until the steps that were in flight have stopped, so the
        // closing figures are the final ones.
        var over = snapshot.IsTerminal && snapshot.Stages.All(s => s.Running == 0);

        if (_bus.IsCompleted(runId))
        {
            // A late publish must not close the story twice; a run that is open again (retry)
            // needs a fresh stream.
            if (snapshot.IsTerminal) return;
            _bus.Reopen(runId);
        }
        _bus.Publish(runId, "programme", "", "progress", snapshot, snapshot.Summary);

        // The bus drops a stream that stayed quiet for hours (a run waiting on sign-off), and
        // the Narrator with it: pick the run up again before saying anything more.
        if (!_narrator.IsTracking(runId))
        {
            var run = await db.WorkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is not null) Follow(run);
        }

        if (over)
        {
            var state = snapshot.State == "CANCELLED" ? "CANCELLED"
                : snapshot.FailedCount > 0 ? "PARTIAL" : "SUCCEEDED";
            _bus.State(runId, "programme", state, snapshot.Summary);
            _bus.Complete(runId);
            LlmWorkScope.ForgetBudget(runId);
        }
    }

    private static WorkQueue Queue(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<WorkQueue>();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
