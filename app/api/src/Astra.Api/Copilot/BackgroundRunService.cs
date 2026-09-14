using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Runs;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// Runs the per-routine pipelines (extraction, scaffold, validation gates)
/// detached from the request that asked for them, narrating on the
/// <see cref="RunEventBus"/> under a fresh run id so a <c>runProgress</c>
/// card — and the Narrator — can follow along. Each run gets its own DI
/// scope with the requesting persona copied in (the pipelines capture
/// <see cref="DevPersonaContext"/> at construction, and a background scope
/// would otherwise default to "Dev User / Engineer").
/// </summary>
public sealed class BackgroundRunService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunEventBus _bus;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BackgroundRunService> _logger;

    public BackgroundRunService(
        IServiceScopeFactory scopeFactory, RunEventBus bus, IHostApplicationLifetime lifetime,
        ILogger<BackgroundRunService> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Guid StartExtraction(Guid subroutineId, string routineName, Persona persona, string displayName)
    {
        var runId = Guid.NewGuid();
        _bus.State(runId, "spec", "RUNNING", $"Reading `{routineName}` and drafting its spec…");
        _ = Task.Run(() => RunExtractionAsync(runId, subroutineId, routineName, persona, displayName));
        return runId;
    }

    public Guid StartScaffold(Guid specId, string routineName, string targetStack, Persona persona, string displayName, string? repairHint = null)
    {
        var runId = Guid.NewGuid();
        _bus.State(runId, "migration", "RUNNING", repairHint is null
            ? $"Generating {targetStack} code for `{routineName}`…"
            : $"Regenerating {targetStack} code for `{routineName}` with the last gate's errors in hand…");
        _ = Task.Run(() => RunScaffoldAsync(runId, specId, routineName, targetStack, persona, displayName, repairHint));
        return runId;
    }

    public Guid StartGate(Guid scaffoldId, string routineName, string gate, Persona persona, string displayName)
    {
        var runId = Guid.NewGuid();
        _bus.State(runId, "validation", "RUNNING", $"Running the {GateLabel(gate)} gate for `{routineName}`…");
        _ = Task.Run(() => RunGateAsync(runId, scaffoldId, routineName, gate, persona, displayName));
        return runId;
    }

    public static string GateLabel(string gate) => gate switch
    {
        "compile" => "compile",
        "test-pack" => "test-pack",
        _ => gate,
    };

    // ── Extraction ───────────────────────────────────────────────────────

    private async Task RunExtractionAsync(Guid runId, Guid subroutineId, string routineName, Persona persona, string displayName)
    {
        var ct = _lifetime.ApplicationStopping;
        try
        {
            using var scope = CreateScope(persona, displayName);
            var pipeline = scope.ServiceProvider.GetRequiredService<ExtractionPipeline>();
            Guid? specId = null;
            string? error = null;
            var tokens = 0;

            await foreach (var evt in pipeline.RunAsync(subroutineId, ct))
            {
                switch (evt.Type)
                {
                    case "stage":
                        var stage = Read(evt.Data);
                        var label = stage.TryGetProperty("label", out var l) ? l.GetString() : stage.TryGetProperty("stage", out var s) ? s.GetString() : null;
                        _bus.Publish(runId, "spec", stage.TryGetProperty("stage", out var st) ? st.GetString() ?? "" : "", "stage", evt.Data, label);
                        break;
                    case "token":
                        if (++tokens % 200 == 0) _bus.Publish(runId, "spec", "writing", "progress", new { tokens }, $"{tokens} tokens written");
                        break;
                    case "warning":
                        _bus.Log(runId, "spec", "", Read(evt.Data).TryGetProperty("message", out var w) ? w.GetString() ?? "warning" : "warning");
                        break;
                    case "error":
                        var e = Read(evt.Data);
                        error = e.TryGetProperty("message", out var m) ? m.GetString() : "extraction failed";
                        break;
                    case "done":
                        var d = Read(evt.Data);
                        if (d.TryGetProperty("specId", out var sid) && Guid.TryParse(sid.GetString(), out var g)) specId = g;
                        break;
                }
            }

            if (specId is { } id)
            {
                _bus.Publish(runId, "spec", "", "item", new { kind = "spec", specId = id, subroutineId, routineName });
                _bus.State(runId, "spec", "SUCCEEDED", $"Spec drafted for `{routineName}`.");
            }
            else
            {
                _bus.State(runId, "spec", "FAILED", error ?? "Extraction produced no spec.");
            }
        }
        catch (OperationCanceledException)
        {
            await RevertExtractingAsync(subroutineId);
            _bus.State(runId, "spec", "FAILED", "Interrupted by an API restart — run it again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background extraction failed for {Sub}", subroutineId);
            await RevertExtractingAsync(subroutineId);
            _bus.State(runId, "spec", "FAILED", Reason(ex));
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    private async Task RevertExtractingAsync(Guid subroutineId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = await db.Subroutines.FirstOrDefaultAsync(s => s.Id == subroutineId);
            if (sub is null || sub.State != "EXTRACTING") return;
            var hasSpec = await db.Specs.AnyAsync(s => s.SubroutineId == subroutineId);
            sub.State = hasSpec ? "DRAFT" : "PARSED";
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not revert EXTRACTING for {Sub}", subroutineId);
        }
    }

    /// <summary>
    /// A run that died after flipping the routine to SCAFFOLDING leaves it
    /// there forever otherwise (seen on Azure: a failed save left
    /// TBlogApplication.ArticleView stuck). Put it back to what the spec's
    /// packages say: SCAFFOLDED when an earlier package exists, else SIGNED.
    /// </summary>
    internal static async Task RevertScaffoldingAsync(IServiceScopeFactory scopes, Guid specId, ILogger logger)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var spec = await db.Specs.Include(s => s.Subroutine).FirstOrDefaultAsync(s => s.Id == specId);
            if (spec?.Subroutine is null || spec.Subroutine.State != "SCAFFOLDING") return;
            var hasPackage = await db.Scaffolds.AnyAsync(s => s.SpecId == specId);
            spec.Subroutine.State = hasPackage ? "SCAFFOLDED" : "SIGNED";
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not revert SCAFFOLDING for spec {Spec}", specId);
        }
    }

    /// <summary>
    /// The message a person can act on: the exception's own text plus the
    /// root cause when one is wrapped inside it. EF's "An error occurred
    /// while saving the entity changes" hides the Postgres error that
    /// actually explains the failure.
    /// </summary>
    internal static string Reason(Exception ex)
    {
        var root = ex.GetBaseException();
        return ReferenceEquals(root, ex) || string.IsNullOrWhiteSpace(root.Message) || ex.Message.Contains(root.Message, StringComparison.Ordinal)
            ? ex.Message
            : $"{ex.Message} — {root.Message}";
    }

    // ── Scaffold ─────────────────────────────────────────────────────────

    private async Task RunScaffoldAsync(Guid runId, Guid specId, string routineName, string targetStack, Persona persona, string displayName, string? repairHint = null)
    {
        var ct = _lifetime.ApplicationStopping;
        try
        {
            using var scope = CreateScope(persona, displayName);
            var pipeline = scope.ServiceProvider.GetRequiredService<ScaffoldPipeline>();
            Guid? scaffoldId = null;
            string? error = null;
            int files = 0, lines = 0, todos = 0, unitRoutines = 0;
            string? unitPath = null;
            var tokens = 0;

            await foreach (var evt in pipeline.RunAsync(specId, targetStack, ct, repairHint))
            {
                switch (evt.Type)
                {
                    case "stage":
                        var stage = Read(evt.Data);
                        _bus.Publish(runId, "migration", stage.TryGetProperty("stage", out var st) ? st.GetString() ?? "" : "", "stage", evt.Data,
                            stage.TryGetProperty("label", out var l) ? l.GetString() : null);
                        break;
                    case "file_started":
                    case "file_start":
                    case "file":
                        var f = Read(evt.Data);
                        _bus.Log(runId, "migration", "", f.TryGetProperty("path", out var p) ? $"Writing {p.GetString()}" : "Writing file");
                        break;
                    case "token":
                        if (++tokens % 200 == 0) _bus.Publish(runId, "migration", "writing", "progress", new { tokens }, $"{tokens} tokens written");
                        break;
                    case "error":
                        var e = Read(evt.Data);
                        error = e.TryGetProperty("message", out var m) ? m.GetString() : "scaffold failed";
                        break;
                    case "done":
                        var d = Read(evt.Data);
                        if (d.TryGetProperty("scaffoldId", out var sid) && Guid.TryParse(sid.GetString(), out var g)) scaffoldId = g;
                        files = d.TryGetProperty("fileCount", out var fc) ? fc.GetInt32() : 0;
                        lines = d.TryGetProperty("totalLines", out var tl) ? tl.GetInt32() : 0;
                        todos = d.TryGetProperty("todoCount", out var tc) ? tc.GetInt32() : 0;
                        unitPath = d.TryGetProperty("unitPath", out var up) && up.ValueKind == JsonValueKind.String ? up.GetString() : null;
                        unitRoutines = d.TryGetProperty("unitRoutineCount", out var ur) && ur.ValueKind == JsonValueKind.Number ? ur.GetInt32() : 0;
                        break;
                }
            }

            if (scaffoldId is { } id)
            {
                _bus.Publish(runId, "migration", "", "item", new { kind = "scaffold", scaffoldId = id, specId, routineName, targetStack, files, lines, todos, unitPath });
                // A faithful 1:1 run converted the whole unit, so the sentence
                // names the unit; the routine only anchored it.
                var summary = unitPath is { Length: > 0 }
                    ? $"Converted `{unitPath}` 1:1 to .NET 10 ({unitRoutines} routines, {files} files, {lines:N0} lines, {todos} TODOs), anchored on `{routineName}`."
                    : $"Generated {files} files ({lines:N0} lines, {todos} TODOs) for `{routineName}` on {targetStack}.";
                _bus.State(runId, "migration", "SUCCEEDED", summary);
            }
            else
            {
                _bus.State(runId, "migration", "FAILED", error ?? "Scaffold produced no package.");
            }
        }
        catch (OperationCanceledException)
        {
            _bus.State(runId, "migration", "FAILED", "Interrupted by an API restart — run it again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background scaffold failed for spec {Spec}", specId);
            await RevertScaffoldingAsync(_scopeFactory, specId, _logger);
            _bus.State(runId, "migration", "FAILED", Reason(ex));
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    // ── Gates ────────────────────────────────────────────────────────────

    private async Task RunGateAsync(Guid runId, Guid scaffoldId, string routineName, string gate, Persona persona, string displayName)
    {
        var ct = _lifetime.ApplicationStopping;
        try
        {
            using var scope = CreateScope(persona, displayName);
            var actor = scope.ServiceProvider.GetRequiredService<DevPersonaContext>();
            Persistence.Entities.ValidationRun run = gate switch
            {
                "compile" => await scope.ServiceProvider.GetRequiredService<CompileValidator>().RunAsync(scaffoldId, actor, ct),
                "test-pack" => await scope.ServiceProvider.GetRequiredService<TestPackValidator>().RunAsync(scaffoldId, actor, ct),
                _ => throw new InvalidOperationException($"Unknown gate '{gate}'."),
            };
            _bus.Publish(runId, "validation", "", "item", new
            {
                kind = "gate", scaffoldId, validationRunId = run.Id, stage = run.Stage, status = run.Status, summary = run.Summary, errorCode = run.ErrorCode,
            });
            _bus.State(runId, "validation",
                run.Status == "PASSED" ? "SUCCEEDED" : "FAILED",
                $"{run.Stage} gate {run.Status}: {run.Summary}");
        }
        catch (OperationCanceledException)
        {
            _bus.State(runId, "validation", "FAILED", "Interrupted by an API restart — run it again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background gate {Gate} failed for scaffold {Scaffold}", gate, scaffoldId);
            _bus.State(runId, "validation", "FAILED", Reason(ex));
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private IServiceScope CreateScope(Persona persona, string displayName)
    {
        var scope = _scopeFactory.CreateScope();
        var actor = scope.ServiceProvider.GetRequiredService<DevPersonaContext>();
        actor.Persona = persona;
        actor.DisplayName = displayName;
        return scope;
    }

    private static JsonElement Read(object? data) =>
        data is JsonElement je ? je : JsonSerializer.SerializeToElement(data, ExtractionJson);

    private static readonly JsonSerializerOptions ExtractionJson = new(JsonSerializerDefaults.Web);
}
