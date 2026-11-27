using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Work;

/// <summary>What a batch is asked to do. <see cref="Routines"/> carry their wave
/// number (0 outside a plan) so leaves are leased first.</summary>
public sealed record WorkRunRequest(
    string Kind,
    Guid CorpusId,
    IReadOnlyList<(Guid SubroutineId, int Wave)> Routines,
    IReadOnlyList<string> Stages,
    string Label,
    string? TargetStack = null,
    decimal? BudgetUsd = null,
    IReadOnlyDictionary<string, int>? Caps = null,
    Guid? ConversationId = null,
    Guid? MigrationWaveId = null);

public sealed record WorkRunPreview(PlanResult Plan, IReadOnlyDictionary<string, int> Caps, IReadOnlyList<string> Warnings);

public sealed record WaveSelection(MigrationPlan Plan, IReadOnlyList<MigrationWave> Waves, IReadOnlyList<(Guid SubroutineId, int Wave)> Routines);

public sealed record WorkError(int Status, string Code, string Message);

public sealed record ReviewQueueItem(
    Guid SubroutineId, string Name, Guid SpecId, string SpecState, Guid CorpusId, string CorpusName,
    DateTimeOffset WaitingSince, Guid? RunId, string? RunLabel, int StepsBlocked);

/// <summary>
/// Creates and controls work runs: expands a wave or a routine list into queued
/// items, enforces who may do it, and answers the review queue. The one path the
/// copilot's batch tools and the REST routes both use.
/// </summary>
public sealed class WorkRunService
{
    public const int MaxRoutinesPerRun = 500;

    private readonly AppDbContext _db;
    private readonly WorkQueue _queue;
    private readonly WorkScheduler _scheduler;
    private readonly ArchetypeRegistry _archetypes;
    private readonly IAuditLogger _audit;

    public WorkRunService(AppDbContext db, WorkQueue queue, WorkScheduler scheduler, ArchetypeRegistry archetypes, IAuditLogger audit)
    {
        _db = db;
        _queue = queue;
        _scheduler = scheduler;
        _archetypes = archetypes;
        _audit = audit;
    }

    /// <summary>Who may queue or control agent work. Reading is open to everyone.</summary>
    public static bool MayRun(Persona persona, IReadOnlyList<string> stages) =>
        persona == Persona.Engineer
        || (persona == Persona.Admin && stages.All(s => s is WorkStages.Extract or WorkStages.Signoff));

    public static bool MayControl(Persona persona) => persona is Persona.Engineer or Persona.Admin;

    // ── selecting routines ───────────────────────────────────────────────

    /// <summary>The routines of one wave (or every wave) of the programme's APPROVED plan.
    /// A draft is not executed: approving a plan is a person's decision.</summary>
    public async Task<(WaveSelection? Selection, WorkError? Error)> SelectWaveAsync(Guid corpusId, int? waveNumber, bool allWaves, IReadOnlyList<string> stages, CancellationToken ct)
    {
        var versionId = await _db.Corpora.AsNoTracking().Where(c => c.Id == corpusId).Select(c => c.LatestVersionId).FirstOrDefaultAsync(ct);
        var plan = await _db.MigrationPlans.AsNoTracking()
            .Where(p => p.CorpusId == corpusId && p.Status == "approved" && p.SourceVersionId == versionId)
            .OrderByDescending(p => p.ApprovedAt).FirstOrDefaultAsync(ct);
        if (plan is null)
        {
            var draft = await _db.MigrationPlans.AsNoTracking().AnyAsync(p => p.CorpusId == corpusId && p.Status == "draft" && p.SourceVersionId == versionId, ct);
            return (null, new(409, "plan.not_approved", draft
                ? "The programme has a draft migration plan but no approved one. Approve it on the migration plan page, then run a wave."
                : "The programme has no migration plan for its current version. Draft one and approve it, then run a wave."));
        }

        var waves = await _db.MigrationWaves.AsNoTracking().Where(w => w.MigrationPlanId == plan.Id).OrderBy(w => w.WaveNumber).ToListAsync(ct);
        List<MigrationWave> chosen;
        if (allWaves) chosen = waves;
        else if (waveNumber is { } n)
        {
            chosen = waves.Where(w => w.WaveNumber == n).ToList();
            if (chosen.Count == 0) return (null, new(404, "wave.not_found", $"The plan has {waves.Count} wave(s); there is no wave {n}."));
        }
        else
        {
            // The next wave: the first one with a routine an agent could still move forward.
            chosen = new();
            foreach (var wave in waves)
            {
                var snapshots = await SnapshotsAsync(corpusId, RoutineIds(wave).Select(id => (id, wave.WaveNumber)).ToList(), ct);
                if (WorkPlanner.Expand(snapshots, stages).Items.Any(i => !WorkStages.IsHuman(i.Kind)))
                {
                    chosen.Add(wave);
                    break;
                }
            }
            if (chosen.Count == 0) return (null, new(409, "wave.nothing_left", "No wave of the plan has anything left for an agent to do at these stages."));
        }

        var routines = chosen.SelectMany(w => RoutineIds(w).Select(id => (id, w.WaveNumber))).ToList();
        return (new WaveSelection(plan, chosen, routines), null);
    }

    public static IReadOnlyList<Guid> RoutineIds(MigrationWave wave)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(wave.PlannedRoutineIdsJson) ?? new())
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    private async Task<List<RoutineSnapshot>> SnapshotsAsync(Guid corpusId, IReadOnlyList<(Guid SubroutineId, int Wave)> routines, CancellationToken ct)
    {
        var ids = routines.Select(r => r.SubroutineId).Distinct().ToArray();
        var rows = await (
            from s in _db.Subroutines.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            join v in _db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
            where v.CorpusId == corpusId && ids.Contains(s.Id)
            select new { s.Id, s.Name, s.State }).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        return routines.Where(r => byId.ContainsKey(r.SubroutineId))
            .GroupBy(r => r.SubroutineId).Select(g => g.First())
            .Select(r => new RoutineSnapshot(r.SubroutineId, byId[r.SubroutineId].Name, byId[r.SubroutineId].State, r.Wave))
            .ToList();
    }

    // ── preview + start ──────────────────────────────────────────────────

    /// <summary>What starting the request would queue, without queueing it (the Confirm card's words).</summary>
    public async Task<WorkRunPreview> PreviewAsync(WorkRunRequest request, CancellationToken ct)
    {
        var snapshots = await SnapshotsAsync(request.CorpusId, request.Routines, ct);

        // A routine already being worked on by an open run is not queued a second time.
        var ids = snapshots.Select(s => s.Id).ToArray();
        var busy = await (
            from w in _db.WorkItems.AsNoTracking()
            join r in _db.WorkRuns.AsNoTracking() on w.RunId equals r.Id
            where ids.Contains(w.SubjectId) && (r.State == "RUNNING" || r.State == "PAUSED")
                  && (w.State == "PENDING" || w.State == "RUNNING" || w.State == "WAITING")
            select new { w.SubjectId, r.Label }).ToListAsync(ct);
        var busyById = busy.GroupBy(b => b.SubjectId).ToDictionary(g => g.Key, g => g.First().Label);

        var plan = WorkPlanner.Expand(snapshots.Where(s => !busyById.ContainsKey(s.Id)), request.Stages);
        var skipped = plan.Skipped.Concat(snapshots.Where(s => busyById.ContainsKey(s.Id))
            .Select(s => new SkippedRoutine(s.Id, s.Name, $"already queued in \"{busyById[s.Id]}\""))).ToList();
        plan = plan with { Skipped = skipped };

        var caps = WorkStages.MergeCaps(WorkStages.DefaultCaps, request.Caps);

        var warnings = new List<string>();
        if (plan.Count(WorkStages.Scaffold) > 0)
        {
            var scaffolding = plan.Items.Where(i => i.Kind == WorkStages.Scaffold).Select(i => i.SubjectId).ToArray();
            var languages = await _db.Subroutines.AsNoTracking().Where(s => scaffolding.Contains(s.Id))
                .Select(s => new { s.Name, s.SourceLanguage }).ToListAsync(ct);
            var projectTarget = Artefacts.ProjectSettings.Parse(await _db.Corpora.AsNoTracking()
                .Where(c => c.Id == request.CorpusId).Select(c => c.SettingsJson).FirstOrDefaultAsync(ct)).TargetStack;
            var blocked = languages.GroupBy(l => l.SourceLanguage ?? "")
                .Select(g => (Count: g.Count(), Target: ScaffoldTargetResolver.Resolve(_archetypes, g.Key, g.First().Name, request.TargetStack, projectTarget)))
                .Where(x => !x.Target.Ok).ToList();
            foreach (var (count, target) in blocked)
                warnings.Add($"{count} code unit(s) cannot be generated: {target.Error}");
        }
        return new WorkRunPreview(plan, caps, warnings);
    }

    public async Task<(WorkRun? Run, WorkRunPreview? Preview, WorkError? Error)> StartAsync(WorkRunRequest request, DevPersonaContext actor, CancellationToken ct)
    {
        if (!MayRun(actor.Persona, request.Stages))
            return (null, null, new(403, "persona.forbidden", "Queueing agent work needs the engineer persona (admin may queue spec drafting only)."));
        if (request.Routines.Count > MaxRoutinesPerRun)
            return (null, null, new(400, "work.too_large", $"A run takes at most {MaxRoutinesPerRun} code units; this one has {request.Routines.Count}. Run it wave by wave."));
        if (request.BudgetUsd is <= 0)
            return (null, null, new(400, "work.invalid_budget", "The cost ceiling must be more than zero."));

        var preview = await PreviewAsync(request, ct);
        if (preview.Plan.Items.Count == 0)
        {
            var why = string.Join("; ", preview.Plan.Skipped.Take(5).Select(s => $"{s.Name}: {s.Reason}"));
            return (null, preview, new(409, "work.nothing_to_do", "There is nothing for an agent to do" + (why.Length > 0 ? $" — {why}." : ".")));
        }

        var now = DateTimeOffset.UtcNow;
        var run = new WorkRun
        {
            Id = Guid.NewGuid(),
            Kind = request.Kind,
            CorpusId = request.CorpusId,
            ConversationId = request.ConversationId,
            MigrationWaveId = request.MigrationWaveId,
            Label = request.Label.Length > 256 ? request.Label[..256] : request.Label,
            State = "RUNNING",
            Persona = actor.Persona.ToString(),
            DisplayName = actor.DisplayName,
            TargetStack = string.IsNullOrWhiteSpace(request.TargetStack) ? null : request.TargetStack.Trim(),
            StagesJson = JsonSerializer.Serialize(request.Stages),
            CapsJson = JsonSerializer.Serialize(preview.Caps),
            BudgetUsd = request.BudgetUsd,
            CreatedAt = now,
        };
        var items = preview.Plan.Items.Select((i, n) => new WorkItem
        {
            Id = i.Id,
            RunId = run.Id,
            Kind = i.Kind,
            SubjectId = i.SubjectId,
            SubjectName = i.SubjectName.Length > 256 ? i.SubjectName[..256] : i.SubjectName,
            DependsOn = i.DependsOn,
            State = WorkStages.IsHuman(i.Kind) ? "WAITING" : "PENDING",
            Lane = "batch",
            Priority = i.Priority,
            MaxAttempts = 3,
            CreatedAt = now.AddTicks(n * 10), // keeps plan order among equal priorities
        }).ToList();
        await _queue.CreateRunAsync(run, items, ct);

        if (request.MigrationWaveId is { } waveId)
            await _db.MigrationWaves.Where(w => w.Id == waveId && w.Status == "planned")
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, "in_progress"), ct);

        await _audit.LogAsync("work_run.started", "corpus", run.CorpusId, actor,
            payload: new { runId = run.Id, run.Kind, routines = preview.Plan.RoutineCount, items = items.Count, stages = request.Stages, run.TargetStack, run.BudgetUsd },
            ct: ct);
        _scheduler.Follow(run);
        _scheduler.Poke(run.Id);
        return (run, preview, null);
    }

    // ── control ──────────────────────────────────────────────────────────

    /// <summary>action: pause | resume | cancel | retry-failed.</summary>
    public async Task<(string? Message, WorkError? Error)> ControlAsync(Guid runId, string action, decimal? budgetUsd, DevPersonaContext actor, CancellationToken ct,
        IReadOnlyDictionary<string, int>? caps = null)
    {
        if (!MayControl(actor.Persona))
            return (null, new(403, "persona.forbidden", "Pausing, resuming, cancelling or retrying a run needs the engineer or admin persona."));
        var run = await _db.WorkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return (null, new(404, "work.run_not_found", "No such run."));

        string message;
        switch (action)
        {
            case "pause":
                if (!await _queue.PauseAsync(runId, $"Paused by {actor.DisplayName}.", ct))
                    return (null, new(409, "work.invalid_state", $"The run is {run.State}; only a running one can be paused."));
                message = "Paused. Steps already running will finish; nothing new starts.";
                break;
            case "resume":
                if (budgetUsd is { } b && b <= run.CostUsd)
                    return (null, new(400, "work.invalid_budget", $"The run has already spent ${run.CostUsd:0.00}; the new ceiling must be higher."));
                if (budgetUsd is null && run.BudgetUsd is { } ceiling && run.CostUsd >= ceiling)
                    return (null, new(409, "work.budget_reached", $"The run spent ${run.CostUsd:0.00} of its ${ceiling:0.00} ceiling. Resume it with a higher ceiling."));
                // New caps (more agents per stage, or fewer to spare a shared sidecar) take effect from the resume.
                string? capsJson = null;
                if (caps is { Count: > 0 })
                {
                    var current = JsonSerializer.Deserialize<Dictionary<string, int>>(run.CapsJson ?? "{}") ?? new();
                    capsJson = JsonSerializer.Serialize(WorkStages.MergeCaps(current, caps));
                }
                if (!await _queue.ResumeAsync(runId, budgetUsd, ct, capsJson))
                    return (null, new(409, "work.invalid_state", $"The run is {run.State}; only a paused one can be resumed."));
                message = capsJson is null ? "Resumed." : $"Resumed with {capsJson}.";
                break;
            case "cancel":
                if (!await _queue.CancelAsync(runId, ct))
                    return (null, new(409, "work.invalid_state", $"The run is already {run.State}."));
                message = "Cancelled. Finished work is kept; queued steps will not run.";
                break;
            case "retry-failed":
                var n = await _queue.RetryFailedAsync(runId, ct);
                if (n == 0) return (null, new(409, "work.nothing_failed", "The run has no failed steps to retry."));
                message = $"Queued {n} failed step(s) again.";
                break;
            default:
                return (null, new(400, "work.unknown_action", "action must be pause, resume, cancel or retry-failed."));
        }

        await _audit.LogAsync($"work_run.{action.Replace('-', '_')}", "corpus", run.CorpusId, actor, payload: new { runId }, ct: ct);
        _scheduler.Follow(run);
        _scheduler.Poke(runId);
        return (message, null);
    }

    /// <summary>The run a sentence means: the id (or its first characters), else the latest open
    /// run of this thread, else of the programme, else the latest of all.</summary>
    public async Task<WorkRun?> FindRunAsync(string? idOrPrefix, Guid? corpusId, Guid? conversationId, CancellationToken ct)
    {
        if (Guid.TryParse(idOrPrefix, out var id))
            return await _db.WorkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

        var runs = _db.WorkRuns.AsNoTracking().AsQueryable();
        if (corpusId is { } c) runs = runs.Where(r => r.CorpusId == c);
        var recent = await runs.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(idOrPrefix))
            return recent.FirstOrDefault(r => r.Id.ToString().StartsWith(idOrPrefix.Trim(), StringComparison.OrdinalIgnoreCase));
        return recent.OrderByDescending(r => r.State is "RUNNING" or "PAUSED")
            .ThenByDescending(r => r.ConversationId == conversationId)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefault();
    }

    // ── the review queue ─────────────────────────────────────────────────

    /// <summary>Specs waiting for an SME, oldest first, each with the run (if any) whose later
    /// steps are held until it is signed. Reviewing and signing happen at the reviewer's pace.</summary>
    public async Task<IReadOnlyList<ReviewQueueItem>> ReviewQueueAsync(Guid? corpusId, int limit, CancellationToken ct)
    {
        var rows = await (
            from s in _db.Subroutines.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            join v in _db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
            join c in _db.Corpora.AsNoTracking() on v.CorpusId equals c.Id
            join sp in _db.Specs.AsNoTracking() on s.Id equals sp.SubroutineId
            where c.LatestVersionId == v.Id && s.State == "IN_REVIEW" && sp.State == "IN_REVIEW"
                  && (corpusId == null || c.Id == corpusId)
            orderby sp.UpdatedAt
            select new { SubroutineId = s.Id, s.Name, SpecId = sp.Id, SpecState = sp.State, CorpusId = c.Id, CorpusName = c.Name, sp.UpdatedAt }
        ).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);

        var ids = rows.Select(r => r.SubroutineId).ToArray();
        var held = await (
            from w in _db.WorkItems.AsNoTracking()
            join r in _db.WorkRuns.AsNoTracking() on w.RunId equals r.Id
            where ids.Contains(w.SubjectId) && (r.State == "RUNNING" || r.State == "PAUSED")
                  && (w.State == "PENDING" || w.State == "WAITING")
            select new { w.SubjectId, w.Kind, r.Id, r.Label }).ToListAsync(ct);

        return rows
            .GroupBy(r => r.SubroutineId).Select(g => g.Last())
            .Select(r =>
            {
                var blocked = held.Where(h => h.SubjectId == r.SubroutineId).ToList();
                var run = blocked.FirstOrDefault();
                return new ReviewQueueItem(r.SubroutineId, r.Name, r.SpecId, r.SpecState, r.CorpusId, r.CorpusName,
                    r.UpdatedAt, run?.Id, run?.Label, blocked.Count(h => !WorkStages.IsHuman(h.Kind)));
            })
            .OrderByDescending(i => i.StepsBlocked > 0).ThenBy(i => i.WaitingSince)
            .ToList();
    }
}
