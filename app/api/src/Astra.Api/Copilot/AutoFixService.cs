using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Astra.Api.Storage;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// "Fix until green": the platform's bounded repair loop for one routine's generated code. Each round runs every gate
/// — COMPILE, then the claim test pack, then QUALITY — and, when any fails, regenerates once with all the failures in
/// one instruction (<see cref="RepairHints"/>), then runs every gate again so a fix that breaks something else is seen
/// at once. It stops when all three pass or after the allowed rounds; the best version seen is kept (a round that
/// makes things worse is rolled back to it) and the gates are re-run on whatever stays, so the recorded results always
/// describe the package that is there. A gate that could not run (ERRORED — the runner, not the code) stops the loop
/// instead of burning a regeneration on it. Every step is narrated on the <see cref="RunEventBus"/>.
/// </summary>
public sealed class AutoFixService
{
    public const int DefaultRounds = 2;
    public const int MaxRounds = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunEventBus _bus;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AutoFixService> _logger;

    public AutoFixService(IServiceScopeFactory scopeFactory, RunEventBus bus, IHostApplicationLifetime lifetime, ILogger<AutoFixService> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Guid Start(Guid specId, string routineName, string targetStack, int rounds, Persona persona, string displayName)
    {
        var runId = Guid.NewGuid();
        rounds = Math.Clamp(rounds, 1, MaxRounds);
        _bus.State(runId, "validation", "RUNNING", $"Fixing `{routineName}` until its gates are green (up to {rounds} regeneration{(rounds == 1 ? "" : "s")})…");
        _ = Task.Run(() => RunAsync(runId, specId, routineName, targetStack, rounds, persona, displayName));
        return runId;
    }

    /// <summary>One round's gate results, and how good the package is: higher is better.</summary>
    internal sealed record RoundResult(IReadOnlyList<ValidationRun> Runs)
    {
        public ValidationRun? Get(string stage) => Runs.FirstOrDefault(r => r.Stage == stage);
        public bool Green => Runs.Count > 0 && new[] { "COMPILE", "TEST_PACK", QualityValidator.Stage }.All(s => Get(s)?.Status == "PASSED");
        public bool RunnerProblem => Runs.Any(r => r.Status == "ERRORED");
        public IReadOnlyList<ValidationRun> Failed => Runs.Where(r => r.Status == "FAILED").ToList();
        public int Score => Scoring(this);
        public string Line => string.Join(" · ", Runs.Select(r => $"{Label(r.Stage)} {r.Status.ToLowerInvariant()}{(r.Status == "PASSED" ? "" : $" ({r.Summary})")}"));
    }

    internal static int Scoring(RoundResult r)
    {
        var score = 0;
        if (r.Get("COMPILE")?.Status == "PASSED") score += 10_000;
        if (r.Get("TEST_PACK") is { } tp)
        {
            if (tp.Status == "PASSED") score += 5_000;
            score += Metric(tp, "passed") * 10 - Metric(tp, "failed") * 10;
        }
        if (r.Get(QualityValidator.Stage) is { } q)
        {
            if (q.Status == "PASSED") score += 1_000;
            score -= Metric(q, "blocking") * 20;
        }
        return score;
    }

    private static int Metric(ValidationRun run, string name)
    {
        if (string.IsNullOrWhiteSpace(run.MetricsJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(run.MetricsJson);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static string Label(string stage) => stage switch
    {
        "COMPILE" => "compile",
        "TEST_PACK" => "test pack",
        "QUALITY" => "quality",
        _ => stage.ToLowerInvariant(),
    };

    private async Task RunAsync(Guid runId, Guid specId, string routineName, string targetStack, int rounds, Persona persona, string displayName)
    {
        var ct = _lifetime.ApplicationStopping;
        try
        {
            using var scope = CreateScope(persona, displayName);
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var blob = sp.GetRequiredService<IBlobClient>();
            var storage = sp.GetRequiredService<StorageOptions>();

            var scaffoldId = await db.Scaffolds.AsNoTracking()
                .Where(s => s.SpecId == specId && s.TargetPlatform == targetStack)
                .OrderByDescending(s => s.GeneratedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (scaffoldId is null)
            {
                _bus.Log(runId, "validation", "generating", $"No {targetStack} code yet for `{routineName}` — generating it first.");
                scaffoldId = await GenerateAsync(runId, sp, specId, targetStack, null, ct);
                if (scaffoldId is null) { _bus.State(runId, "validation", "FAILED", $"Could not generate code for `{routineName}`."); return; }
            }

            (int Score, int Round, string SnapshotUri)? best = null;
            RoundResult? last = null;
            for (var round = 0; ; round++)
            {
                if (round > 0)
                {
                    var hint = await RepairHints.BuildAsync(db, blob, last!.Failed, ct);
                    _bus.Publish(runId, "validation", "regenerating", "stage", new { round, rounds },
                        $"Round {round} of {rounds}: regenerating with {last.Failed.Count} failing gate{(last.Failed.Count == 1 ? "" : "s")} in hand…");
                    var regenerated = await GenerateAsync(runId, sp, specId, targetStack, hint, ct);
                    if (regenerated is null)
                    {
                        _bus.Log(runId, "validation", "regenerating", "The regeneration produced no package; keeping the best version so far.");
                        await RestoreAsync(runId, sp, db, blob, scaffoldId.Value, best, ct);
                        _bus.State(runId, "validation", "FAILED", $"Stopped after round {round}: the regeneration failed. The best version (round {best?.Round ?? 0}) is kept.");
                        return;
                    }
                    scaffoldId = regenerated;
                }

                var result = await RunGatesAsync(runId, sp, scaffoldId.Value, ct);
                last = result;
                _bus.Publish(runId, "validation", "gates", "item", new { kind = "autofixRound", round, scaffoldId, score = result.Score, green = result.Green },
                    $"{(round == 0 ? "Starting point" : $"After round {round}")}: {result.Line}");

                if (best is null || result.Score > best.Value.Score)
                    best = (result.Score, round, await SnapshotAsync(db, blob, storage, scaffoldId.Value, runId, round, ct));

                if (result.Green)
                {
                    _bus.State(runId, "validation", "SUCCEEDED", round == 0
                        ? $"`{routineName}` is already green: compile, test pack and quality all pass."
                        : $"`{routineName}` is green after {round} regeneration{(round == 1 ? "" : "s")}: compile, test pack and quality all pass.");
                    return;
                }
                if (result.RunnerProblem)
                {
                    var errored = result.Runs.First(r => r.Status == "ERRORED");
                    _bus.State(runId, "validation", "FAILED", $"Stopped: the {Label(errored.Stage)} gate could not run ({errored.Summary}). That is the platform, not the code — run it again once the runner is back.");
                    return;
                }
                if (round >= rounds)
                {
                    if (best is { } b && b.Round != round)
                    {
                        _bus.Log(runId, "validation", "restoring", $"Round {round} was worse than round {b.Round}; restoring round {b.Round}'s code and re-running its gates.");
                        await RestoreAsync(runId, sp, db, blob, scaffoldId.Value, best, ct);
                        result = await RunGatesAsync(runId, sp, scaffoldId.Value, ct);
                    }
                    _bus.State(runId, "validation", "FAILED",
                        $"Still not green after {rounds} regeneration{(rounds == 1 ? "" : "s")} — this needs a person. What remains: {result.Line}");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _bus.State(runId, "validation", "FAILED", "Interrupted by an API restart — run it again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-fix loop failed for spec {Spec}", specId);
            _bus.State(runId, "validation", "FAILED", $"The fix loop stopped: {ex.Message}");
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    private async Task<RoundResult> RunGatesAsync(Guid runId, IServiceProvider sp, Guid scaffoldId, CancellationToken ct)
    {
        var actor = sp.GetRequiredService<DevPersonaContext>();
        var runs = new List<ValidationRun>();
        _bus.Log(runId, "validation", "gates", "Running the compile gate…");
        var compile = await sp.GetRequiredService<CompileValidator>().RunAsync(scaffoldId, actor, ct);
        runs.Add(compile);
        if (compile.Status == "PASSED")
        {
            _bus.Log(runId, "validation", "gates", "Generating and running the claim test pack…");
            await sp.GetRequiredService<TestPackGenerator>().GenerateAsync(scaffoldId, actor, ct);
            runs.Add(await sp.GetRequiredService<TestPackValidator>().RunAsync(scaffoldId, actor, ct));
        }
        _bus.Log(runId, "validation", "gates", "Running the quality checks…");
        runs.Add(await sp.GetRequiredService<QualityValidator>().RunAsync(scaffoldId, actor, ct, aiReview: false));
        return new RoundResult(runs);
    }

    private async Task<Guid?> GenerateAsync(Guid runId, IServiceProvider sp, Guid specId, string targetStack, string? hint, CancellationToken ct)
    {
        var pipeline = sp.GetRequiredService<ScaffoldPipeline>();
        Guid? scaffoldId = null;
        await foreach (var evt in pipeline.RunAsync(specId, targetStack, ct, hint))
        {
            if (evt.Type == "done")
            {
                var d = evt.Data is JsonElement je ? je : JsonSerializer.SerializeToElement(evt.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (d.TryGetProperty("scaffoldId", out var sid) && Guid.TryParse(sid.GetString(), out var g)) scaffoldId = g;
            }
            else if (evt.Type == "error")
            {
                var e = evt.Data is JsonElement je2 ? je2 : JsonSerializer.SerializeToElement(evt.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                _bus.Log(runId, "validation", "regenerating", $"Generation error: {(e.TryGetProperty("message", out var m) ? m.GetString() : "unknown")}");
            }
        }
        return scaffoldId;
    }

    /// <summary>Keeps a copy of the package as it is now, so a later, worse round can be rolled back to it.</summary>
    private static async Task<string> SnapshotAsync(AppDbContext db, IBlobClient blob, StorageOptions storage, Guid scaffoldId, Guid runId, int round, CancellationToken ct)
    {
        var scaffold = await db.Scaffolds.AsNoTracking().FirstAsync(s => s.Id == scaffoldId, ct);
        var manifest = await blob.GetTextAsync(scaffold.PackageBlobUri, ct);
        return await blob.PutTextAsync(storage.Buckets.Scaffolds, $"{scaffoldId}/autofix/{runId:N}-round{round}.json", manifest, "application/json", ct);
    }

    private async Task RestoreAsync(Guid runId, IServiceProvider sp, AppDbContext db, IBlobClient blob, Guid scaffoldId, (int Score, int Round, string SnapshotUri)? best, CancellationToken ct)
    {
        if (best is not { } b) return;
        var storage = sp.GetRequiredService<StorageOptions>();
        var manifest = await blob.GetTextAsync(b.SnapshotUri, ct);
        var uri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"{scaffoldId}/manifest.json", manifest, "application/json", ct);
        var scaffold = await db.Scaffolds.FirstAsync(s => s.Id == scaffoldId, ct);
        scaffold.PackageBlobUri = uri;
        await db.SaveChangesAsync(ct);
        _bus.Log(runId, "validation", "restoring", $"Restored the code from round {b.Round}.");
    }

    private IServiceScope CreateScope(Persona persona, string displayName)
    {
        var scope = _scopeFactory.CreateScope();
        var actor = scope.ServiceProvider.GetRequiredService<DevPersonaContext>();
        actor.Persona = persona;
        actor.DisplayName = displayName;
        return scope;
    }
}
