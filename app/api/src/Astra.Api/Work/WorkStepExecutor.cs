using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Work;

public enum StepVerdict
{
    Succeeded,
    Failed,
    /// <summary>The gate does not apply to this package: closed, never reported as passed.</summary>
    Skipped,
    /// <summary>Not startable right now (somebody else holds the routine); try again later at no cost to its attempts.</summary>
    Busy,
}

public sealed record StepOutcome(StepVerdict Verdict, string? Error = null, object? Result = null, bool Retryable = false)
{
    public static StepOutcome Ok(object? result = null) => new(StepVerdict.Succeeded, Result: result);
    public static StepOutcome Fail(string error, bool retryable = false, object? result = null) => new(StepVerdict.Failed, error, result, retryable);
}

/// <summary>
/// Runs one leased item through the same pipelines the REST routes and the
/// single-routine copilot tools use, under the requesting persona. Every step
/// is safe to run again after an interruption: work that already finished is
/// recognised and not paid for twice, and a routine left mid-step by a dead
/// process is put back first. There is deliberately no signing step.
/// </summary>
public sealed class WorkStepExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorkStepExecutor> _logger;
    // Faithful 1:1 converts a whole source unit; two routines of one unit must not convert it at once.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _unitLocks = new();

    public WorkStepExecutor(IServiceScopeFactory scopes, ILogger<WorkStepExecutor> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<StepOutcome> RunAsync(WorkRun run, WorkItem item, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var actor = scope.ServiceProvider.GetRequiredService<DevPersonaContext>();
        actor.Persona = Enum.TryParse<Persona>(run.Persona, ignoreCase: true, out var p) ? p : Persona.Engineer;
        actor.DisplayName = run.DisplayName;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var routine = await db.Subroutines.AsNoTracking().Include(s => s.SourceFile)
            .FirstOrDefaultAsync(s => s.Id == item.SubjectId, ct);
        if (routine is null) return StepOutcome.Fail("The routine no longer exists (the programme was re-synced).");

        return item.Kind switch
        {
            WorkStages.Extract => await ExtractAsync(scope.ServiceProvider, db, item, routine, ct),
            WorkStages.Scaffold => await ScaffoldAsync(scope.ServiceProvider, db, run, item, routine, ct),
            WorkStages.Compile or WorkStages.TestPack => await GateAsync(scope.ServiceProvider, db, run, item, routine, actor, ct),
            _ => StepOutcome.Fail($"No worker runs '{item.Kind}' steps."),
        };
    }

    // ── extract ──────────────────────────────────────────────────────────

    private async Task<StepOutcome> ExtractAsync(IServiceProvider services, AppDbContext db, WorkItem item, Subroutine routine, CancellationToken ct)
    {
        if (routine.State == "EXTRACTING")
        {
            // A first attempt that finds the routine busy was started elsewhere; a later one
            // is cleaning up after the process that held it.
            if (item.Attempts <= 1) return new StepOutcome(StepVerdict.Busy, "A spec for this routine is being drafted elsewhere.");
            await RevertExtractingAsync(routine.Id);
            routine.State = await db.Subroutines.AsNoTracking().Where(s => s.Id == routine.Id).Select(s => s.State).FirstAsync(ct);
        }

        var existing = await db.Specs.AsNoTracking().Where(s => s.SubroutineId == routine.Id)
            .OrderByDescending(s => s.CreatedAt).Select(s => new { s.Id, s.CreatedAt }).FirstOrDefaultAsync(ct);
        if (routine.State != "PARSED" && !(routine.State == "DRAFT" && existing is not null && existing.CreatedAt < item.CreatedAt))
            return StepOutcome.Ok(new { specId = existing?.Id, reused = true });

        var pipeline = services.GetRequiredService<ExtractionPipeline>();
        Guid? specId = null;
        string? error = null;
        var retryable = true;
        try
        {
            await foreach (var evt in pipeline.RunAsync(routine.Id, ct))
            {
                if (evt.Type == "error")
                {
                    var e = Read(evt.Data);
                    error = e.TryGetProperty("message", out var m) ? m.GetString() : "extraction failed";
                    if (e.TryGetProperty("retryable", out var r) && r.ValueKind == JsonValueKind.False) retryable = false;
                }
                else if (evt.Type == "done")
                {
                    var d = Read(evt.Data);
                    if (d.TryGetProperty("specId", out var sid) && Guid.TryParse(sid.GetString(), out var g)) specId = g;
                }
            }
        }
        catch
        {
            await RevertExtractingAsync(routine.Id);
            throw;
        }

        if (specId is { } id) return StepOutcome.Ok(new { specId = id });
        await RevertExtractingAsync(routine.Id);
        ct.ThrowIfCancellationRequested();
        return StepOutcome.Fail(error ?? "Extraction produced no spec.", retryable);
    }

    private async Task RevertExtractingAsync(Guid subroutineId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = await db.Subroutines.FirstOrDefaultAsync(s => s.Id == subroutineId);
            if (sub is null || sub.State != "EXTRACTING") return;
            sub.State = await db.Specs.Where(s => s.SubroutineId == subroutineId)
                .OrderByDescending(s => s.CreatedAt).Select(s => s.State).FirstOrDefaultAsync() ?? "PARSED";
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not revert EXTRACTING for {Sub}", subroutineId);
        }
    }

    // ── scaffold ─────────────────────────────────────────────────────────

    private async Task<StepOutcome> ScaffoldAsync(IServiceProvider services, AppDbContext db, WorkRun run, WorkItem item, Subroutine routine, CancellationToken ct)
    {
        var spec = await db.Specs.AsNoTracking().Where(s => s.SubroutineId == routine.Id)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (spec is null || spec.State != "SIGNED")
            return StepOutcome.Fail($"The spec is {spec?.State ?? "missing"}; only a signed spec can be generated from.");

        var target = ScaffoldTargetResolver.Resolve(
            services.GetRequiredService<ArchetypeRegistry>(), routine.SourceLanguage ?? "", routine.Name, run.TargetStack);
        if (!target.Ok) return StepOutcome.Fail(target.Error!);

        // Already generated for this item (the process died after saving the package).
        var done = await db.Scaffolds.AsNoTracking()
            .Where(s => s.SpecId == spec.Id && s.TargetPlatform == target.TargetStack && s.GeneratedAt >= item.CreatedAt)
            .Select(s => new { s.Id }).FirstOrDefaultAsync(ct);
        if (done is not null)
        {
            await Copilot.BackgroundRunService.RevertScaffoldingAsync(_scopes, spec.Id, _logger);
            return StepOutcome.Ok(new { scaffoldId = done.Id, targetStack = target.TargetStack, reused = true });
        }

        if (routine.State == "SCAFFOLDING")
        {
            if (item.Attempts <= 1) return new StepOutcome(StepVerdict.Busy, "Code for this routine is being generated elsewhere.");
            await Copilot.BackgroundRunService.RevertScaffoldingAsync(_scopes, spec.Id, _logger);
        }

        SemaphoreSlim? unit = null;
        if (FaithfulConversion.IsFaithful(target.TargetStack))
        {
            unit = _unitLocks.GetOrAdd(routine.SourceFileId, _ => new SemaphoreSlim(1, 1));
            await unit.WaitAsync(ct);
        }
        try
        {
            var pipeline = services.GetRequiredService<ScaffoldPipeline>();
            Guid? scaffoldId = null;
            string? error = null;
            var files = 0;
            var todos = 0;
            await foreach (var evt in pipeline.RunAsync(spec.Id, target.TargetStack, ct))
            {
                if (evt.Type == "error")
                {
                    var e = Read(evt.Data);
                    error ??= e.TryGetProperty("message", out var m) ? m.GetString() : "scaffold failed";
                }
                else if (evt.Type == "done")
                {
                    var d = Read(evt.Data);
                    if (d.TryGetProperty("scaffoldId", out var sid) && Guid.TryParse(sid.GetString(), out var g)) scaffoldId = g;
                    if (d.TryGetProperty("fileCount", out var fc)) files = fc.GetInt32();
                    if (d.TryGetProperty("todoCount", out var tc)) todos = tc.GetInt32();
                }
            }
            if (scaffoldId is { } id) return StepOutcome.Ok(new { scaffoldId = id, targetStack = target.TargetStack, files, todos });
            await Copilot.BackgroundRunService.RevertScaffoldingAsync(_scopes, spec.Id, _logger);
            ct.ThrowIfCancellationRequested();
            return StepOutcome.Fail(error ?? "Scaffold produced no package.", retryable: true);
        }
        catch
        {
            await Copilot.BackgroundRunService.RevertScaffoldingAsync(_scopes, spec.Id, _logger);
            throw;
        }
        finally
        {
            unit?.Release();
        }
    }

    // ── gates ────────────────────────────────────────────────────────────

    private static async Task<StepOutcome> GateAsync(IServiceProvider services, AppDbContext db, WorkRun run, WorkItem item, Subroutine routine, DevPersonaContext actor, CancellationToken ct)
    {
        var query = db.Scaffolds.AsNoTracking()
            .Where(s => db.Specs.Any(sp => sp.Id == s.SpecId && sp.SubroutineId == routine.Id));
        if (!string.IsNullOrWhiteSpace(run.TargetStack)) query = query.Where(s => s.TargetPlatform == run.TargetStack);
        var scaffold = await query.OrderByDescending(s => s.GeneratedAt).Select(s => new { s.Id }).FirstOrDefaultAsync(ct);
        if (scaffold is null) return StepOutcome.Fail("No generated code to check — generate it first.");

        var stage = item.Kind == WorkStages.Compile ? "COMPILE" : "TEST_PACK";
        var passed = await db.ValidationRuns.AsNoTracking()
            .Where(v => v.ScaffoldId == scaffold.Id && v.Stage == stage && v.Status == "PASSED" && v.StartedAt >= item.CreatedAt)
            .Select(v => new { v.Id, v.Summary }).FirstOrDefaultAsync(ct);
        if (passed is not null)
            return StepOutcome.Ok(new { scaffoldId = scaffold.Id, validationRunId = passed.Id, status = "PASSED", summary = passed.Summary, reused = true });

        if (item.Attempts > 1)
            await CloseRunningGatesAsync(db, scaffold.Id, stage, item.CreatedAt, "Interrupted by an API restart.");

        ValidationRun result;
        try
        {
            result = item.Kind == WorkStages.Compile
                ? await services.GetRequiredService<CompileValidator>().RunAsync(scaffold.Id, actor, ct)
                : await services.GetRequiredService<TestPackValidator>().RunAsync(scaffold.Id, actor, ct);
            ct.ThrowIfCancellationRequested();
        }
        catch when (ct.IsCancellationRequested)
        {
            // The validator cannot save its own row once its token is cancelled.
            await CloseRunningGatesAsync(db, scaffold.Id, stage, item.CreatedAt, "Stopped before it finished.");
            throw;
        }

        var payload = new { scaffoldId = scaffold.Id, validationRunId = result.Id, status = result.Status, summary = result.Summary };
        return result.Status switch
        {
            "PASSED" => StepOutcome.Ok(payload),
            "SKIPPED" => new StepOutcome(StepVerdict.Skipped, $"{stage} gate skipped: {result.Summary}", payload),
            "ERRORED" => StepOutcome.Fail($"{stage} gate ERRORED: {result.Summary}", retryable: true, payload),
            _ => StepOutcome.Fail($"{stage} gate {result.Status}: {result.Summary}", retryable: false, payload),
        };
    }

    /// <summary>Gate rows left RUNNING by an interrupted step would show as in flight forever.</summary>
    private static async Task CloseRunningGatesAsync(AppDbContext db, Guid scaffoldId, string stage, DateTimeOffset since, string why)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ValidationRuns
            .Where(v => v.ScaffoldId == scaffoldId && v.Stage == stage && v.Status == "RUNNING" && v.StartedAt >= since)
            .ExecuteUpdateAsync(u => u
                .SetProperty(v => v.Status, "ERRORED")
                .SetProperty(v => v.Summary, why)
                .SetProperty(v => v.CompletedAt, now), CancellationToken.None);
    }

    private static JsonElement Read(object? data) =>
        data is JsonElement je ? je : JsonSerializer.SerializeToElement(data, Json);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
