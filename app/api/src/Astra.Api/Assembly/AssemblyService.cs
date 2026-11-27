using System.Text;
using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Astra.Api.Storage;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Assembly;

/// <summary>What to assemble: the module's code units by path prefix, wave or explicit ids (none = every unit).</summary>
public sealed record AssemblyRequest(
    string? PathPrefix = null,
    Guid? WaveId = null,
    IReadOnlyList<Guid>? UnitIds = null,
    string? TargetStack = null,
    bool RequireTests = false,
    string? Name = null);

/// <summary>A unit the request matched but the application could not include, and why.</summary>
public sealed record ExcludedUnit(Guid UnitId, string UnitName, string SourcePath, string Reason);

/// <summary>
/// Builds one application from a module's code units: picks each unit's latest package for the target stack that
/// passed its gates, merges them with the stack's <see cref="IModuleAssembler"/>, stores the project, then builds and
/// tests it once through the gate pool. A background run on the <see cref="RunEventBus"/> (agent "migration"), so the
/// conversation narrates it and no request waits on a build.
/// </summary>
public sealed class AssemblyService
{
    public const string Agent = "migration";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunEventBus _bus;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AssemblyService> _logger;

    public AssemblyService(IServiceScopeFactory scopeFactory, RunEventBus bus, IHostApplicationLifetime lifetime, ILogger<AssemblyService> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>A name for a module from its request: the path's last folder, the wave name, or the project.</summary>
    public static string DefaultName(AssemblyRequest r, string corpusName, string? waveName) =>
        r.Name is { Length: > 0 } n ? n.Trim()
        : r.PathPrefix is { Length: > 0 } p ? $"{corpusName} — {p.TrimEnd('/').Split('/').Last()}"
        : waveName is { Length: > 0 } w ? $"{corpusName} — {w}"
        : corpusName;

    /// <summary>Creates the assembly row and starts the run; the run id is the assembly id.</summary>
    public async Task<ModuleAssembly> StartAsync(AppDbContext db, Corpus corpus, AssemblyRequest request, Persona persona, string displayName, CancellationToken ct)
    {
        var target = (request.TargetStack ?? "java-spring").Trim().ToLowerInvariant();
        string? waveName = null;
        if (request.WaveId is { } wid)
            waveName = await db.MigrationWaves.AsNoTracking().Where(w => w.Id == wid).Select(w => w.Name).FirstOrDefaultAsync(ct);
        var row = new ModuleAssembly
        {
            Id = Guid.NewGuid(),
            CorpusId = corpus.Id,
            SourceVersionId = corpus.LatestVersionId,
            Name = DefaultName(request, corpus.Name, waveName),
            TargetStack = target,
            State = "RUNNING",
            SelectionJson = JsonSerializer.Serialize(new { request.PathPrefix, request.WaveId, request.UnitIds, request.RequireTests }, Json),
            Summary = "Starting…",
            CreatedBy = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ModuleAssemblies.Add(row);
        await db.SaveChangesAsync(ct);
        _bus.State(row.Id, Agent, "RUNNING", $"Assembling {row.Name} into one {target} application…");
        _ = Task.Run(() => RunAsync(row.Id, request with { TargetStack = target }, persona, displayName));
        return row;
    }

    private async Task RunAsync(Guid id, AssemblyRequest request, Persona persona, string displayName)
    {
        var ct = _lifetime.ApplicationStopping;
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var actor = sp.GetRequiredService<DevPersonaContext>();
        actor.Persona = persona;
        actor.DisplayName = displayName;
        var db = sp.GetRequiredService<AppDbContext>();
        var row = await db.ModuleAssemblies.FirstAsync(a => a.Id == id, ct);
        try
        {
            var assembler = ModuleAssemblers.For(row.TargetStack);
            if (assembler is null)
            {
                await FinishAsync(db, row, "FAILED", $"{row.TargetStack} code cannot be assembled yet (supported: {string.Join(", ", ModuleAssemblers.Supported)}).", ct);
                return;
            }

            // ── select ──
            _bus.Publish(id, Agent, "select", "stage", null, "Choosing each code unit's latest checked package…");
            var (included, excluded) = await SelectAsync(db, sp.GetRequiredService<IBlobClient>(), row, request, ct);
            row.UnitCount = included.Count;
            row.ExcludedCount = excluded.Count;
            if (included.Count == 0)
            {
                row.ReportJson = JsonSerializer.Serialize(new { excluded }, Json);
                var why = excluded.Count == 0 ? "no code units matched" : $"none of the {excluded.Count} matching code units has a {(request.RequireTests ? "package whose tests pass" : "package that compiles")}";
                await FinishAsync(db, row, "FAILED", $"Nothing to assemble: {why}.", ct);
                return;
            }
            _bus.Log(id, Agent, "select", $"{included.Count} code unit(s) included, {excluded.Count} left out.");

            // ── assemble ──
            _bus.Publish(id, Agent, "assemble", "stage", null, $"Merging {included.Count} packages into one project…");
            var module = assembler.Assemble(included, new AssemblyOptions(row.Name));
            row.FileCount = module.Files.Count;
            row.ReportJson = JsonSerializer.Serialize(new { report = module.Report, excluded }, Json);
            var blob = sp.GetRequiredService<IBlobClient>();
            var storage = sp.GetRequiredService<StorageOptions>();
            var manifest = JsonSerializer.Serialize(new
            {
                assemblyId = row.Id,
                name = row.Name,
                targetStack = row.TargetStack,
                files = module.Files.Select(f => new { path = f.Key, content = f.Value }),
            }, Json);
            row.PackageBlobUri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"assemblies/{row.Id:N}/package.json", manifest, "application/json", ct);
            row.Summary = $"Assembled {included.Count} code units into {module.Files.Count} files; building…";
            await db.SaveChangesAsync(ct);
            _bus.Log(id, Agent, "assemble",
                $"{module.Report.Shared.Count} item(s) held once, {module.Report.Conflicts.Count} disagreement(s) recorded.");

            // ── compile + test, one gate slot ──
            if (string.Equals(row.TargetStack, Astra.Api.Llm.FaithfulConversion.Stack, StringComparison.OrdinalIgnoreCase))
                await DotnetGateAsync(db, blob, storage, row, module, ct);
            else
                await GateAsync(db, sp.GetRequiredService<MavenClient>(), blob, storage, row, module, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            row.State = "ERRORED";
            row.Summary = "Stopped: the API restarted while this assembly ran. Start it again.";
            row.CompletedAt = DateTimeOffset.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assembly {Id} failed", id);
            await FinishAsync(db, row, "ERRORED", $"The assembly could not run: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task<(List<UnitPackage> Included, List<ExcludedUnit> Excluded)> SelectAsync(
        AppDbContext db, IBlobClient blob, ModuleAssembly row, AssemblyRequest request, CancellationToken ct)
    {
        var q = db.Subroutines.AsNoTracking().Where(s => s.SourceFile!.SourceVersion!.CorpusId == row.CorpusId);
        var ids = request.UnitIds?.ToList() ?? new List<Guid>();
        if (request.WaveId is { } wid)
        {
            var planned = await db.MigrationWaves.AsNoTracking().Where(w => w.Id == wid).Select(w => w.PlannedRoutineIdsJson).FirstOrDefaultAsync(ct);
            if (planned is not null)
                try { ids.AddRange(JsonSerializer.Deserialize<List<Guid>>(planned) ?? new()); } catch (JsonException) { }
        }
        if (ids.Count > 0) q = q.Where(s => ids.Contains(s.Id));
        else if (row.SourceVersionId is { } vid) q = q.Where(s => s.SourceFile!.SourceVersionId == vid);
        if (request.PathPrefix is { Length: > 0 } prefix)
        {
            var p = prefix.Replace('\\', '/').Trim().TrimStart('/');
            if (p.Contains('/'))
                q = q.Where(s => s.SourceFile!.RelativePath.StartsWith(p));
            else
            {
                // A bare module name ("societe") means that folder wherever it sits: societe/… or htdocs/societe/….
                var folder = p + "/";
                var inner = "/" + p + "/";
                q = q.Where(s => s.SourceFile!.RelativePath.StartsWith(folder) || s.SourceFile!.RelativePath.Contains(inner));
            }
        }
        var units = await q.OrderBy(s => s.SourceFile!.RelativePath).ThenBy(s => s.LineStart)
            .Select(s => new { s.Id, s.Name, Path = s.SourceFile!.RelativePath }).ToListAsync(ct);

        var unitIds = units.Select(u => u.Id).ToList();
        var scaffolds = await db.Scaffolds.AsNoTracking()
            .Where(s => s.TargetPlatform == row.TargetStack && unitIds.Contains(s.Spec!.SubroutineId))
            .Select(s => new { s.Id, s.Spec!.SubroutineId, s.GeneratedAt })
            .ToListAsync(ct);
        var latest = scaffolds.GroupBy(s => s.SubroutineId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.GeneratedAt).First().Id);
        var generatedAt = scaffolds.ToDictionary(s => s.Id, s => s.GeneratedAt);
        var scaffoldIds = latest.Values.ToList();
        var runs = await db.ValidationRuns.AsNoTracking()
            .Where(v => scaffoldIds.Contains(v.ScaffoldId) && (v.Stage == "COMPILE" || v.Stage == "TEST_PACK"))
            .Select(v => new { v.ScaffoldId, v.Stage, v.Status, v.StartedAt })
            .ToListAsync(ct);
        var gate = runs.GroupBy(v => (v.ScaffoldId, v.Stage)).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.StartedAt).First().Status);

        var included = new List<UnitPackage>();
        var excluded = new List<ExcludedUnit>();
        foreach (var u in units)
        {
            if (!latest.TryGetValue(u.Id, out var sid)) { excluded.Add(new(u.Id, u.Name, u.Path, $"no {row.TargetStack} code generated yet")); continue; }
            var compile = gate.GetValueOrDefault((sid, "COMPILE"));
            var tests = gate.GetValueOrDefault((sid, "TEST_PACK"));
            if (compile != "PASSED") { excluded.Add(new(u.Id, u.Name, u.Path, compile is null ? "its code has not been compiled yet" : $"its code does not compile (latest compile: {compile.ToLowerInvariant()})")); continue; }
            if (request.RequireTests && tests != "PASSED") { excluded.Add(new(u.Id, u.Name, u.Path, tests is null ? "its test pack has not run yet" : $"its tests do not pass (latest test pack: {tests.ToLowerInvariant()})")); continue; }
            var files = await RepairHints.ReadPackageAsync(db, blob, sid, ct);
            if (files.Count == 0) { excluded.Add(new(u.Id, u.Name, u.Path, "its package could not be read")); continue; }
            included.Add(new UnitPackage(u.Id, u.Name, u.Path, sid, files, generatedAt.GetValueOrDefault(sid)));
        }
        return (included, excluded);
    }

    private async Task GateAsync(AppDbContext db, MavenClient maven, IBlobClient blob, StorageOptions storage, ModuleAssembly row, AssembledModule module, CancellationToken ct)
    {
        _bus.Publish(row.Id, Agent, "compile", "stage", null, "Waiting for a build slot…");
        using var slot = await GateExecutorPool.Shared.AcquireAsync(ct, ahead =>
        {
            _bus.Log(row.Id, Agent, "compile", $"Waiting for a build slot ({ahead} queued).");
            return Task.CompletedTask;
        });
        if (!await maven.PingAsync(ct))
        {
            row.CompileStatus = "ERRORED";
            await FinishAsync(db, row, "ERRORED", "Assembled, but the Java build service is unreachable, so it was not built or tested. Download it, or try again later.", ct);
            return;
        }
        _bus.Publish(row.Id, Agent, "compile", "stage", null, "Building the assembled application…");
        var sources = module.Files.Where(f => !f.Key.StartsWith("units/", StringComparison.Ordinal) && f.Key != "ASSEMBLY.md")
            .Select(f => new MavenClient.JavaSource(f.Key, f.Value)).ToList();
        var compile = await maven.CompileAsync(new MavenClient.CompileRequest(sources), ct);
        slot.Dispose();   // tests take a slot per batch below, so a long module does not hold other gates for its whole run

        var log = new StringBuilder();
        log.AppendLine("=== mvn compile ===").AppendLine(compile.Log)
            .AppendLine($"=== compile exit {compile.ExitCode} · {compile.ErrorCount} error(s) · {compile.DurationMs} ms ===");
        row.CompileStatus = compile.ExitCode == 0 ? "PASSED" : "FAILED";
        if (row.CompileStatus == "FAILED")
        {
            row.GateLogBlobUri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"assemblies/{row.Id:N}/gate.log", log.ToString(), "text/plain", ct);
            await FinishAsync(db, row, "FAILED", $"Assembled {row.UnitCount} code units, but the application does not build ({compile.ErrorCount} compile error(s)). The log says where.", ct);
            return;
        }

        // Tests run a few units at a time: one request per batch stays well inside the build service's request limit
        // (App Service cuts a request at ~230 s), however many units the module has. Each unit's tests start only that
        // unit (see JavaSpringAssembler's UnitTestApplication), so batching changes nothing about what they test.
        var batches = TestBatches(module.Report.Units.Select(u => u.Package).ToList(), UnitsPerTestBatch);
        int total = 0, failed = 0, timedOutBatches = 0, done = 0;
        var failingBatches = new List<string>();
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            _bus.Progress(row.Id, Agent, "test", done, module.Report.Units.Count, failingBatches.Count);
            _bus.Log(row.Id, Agent, "test", $"Testing code units {done + 1}–{done + batch.Count} of {module.Report.Units.Count}…");
            using var testSlot = await GateExecutorPool.Shared.AcquireAsync(ct, _ => Task.CompletedTask);
            var args = new List<string>
            {
                "-Dtest=" + string.Join(",", batch.Select(p => p.Replace('.', '/') + "/**/*")),
                "-Dsurefire.failIfNoSpecifiedTests=false", "-DfailIfNoTests=false",
            };
            var t = await maven.TestAsync(new MavenClient.TestRequest(compile.ArtifactId, args, TimeoutMs: BatchTimeoutMs), ct);
            total += t.Tests;
            failed += t.Failures + t.Errors;
            if (t.TimedOut) timedOutBatches++;
            if (t.TimedOut || t.Failures + t.Errors > 0 || (t.ExitCode != 0 && t.Tests == 0))
                failingBatches.Add(string.Join(", ", batch.Select(p => p[(p.LastIndexOf('.') + 1)..])));
            log.AppendLine().AppendLine($"=== mvn test · units {done + 1}–{done + batch.Count}: {string.Join(", ", batch)} ===")
                .AppendLine(Tail(t.Stdout, 60_000));
            if (!string.IsNullOrWhiteSpace(t.Stderr)) log.AppendLine("--- stderr ---").AppendLine(Tail(t.Stderr, 10_000));
            log.AppendLine($"=== batch: {t.Tests} test(s), {t.Failures} failure(s), {t.Errors} error(s) · exit {t.ExitCode} · {t.DurationMs} ms{(t.TimedOut ? " · timed out" : "")} ===");
            done += batch.Count;
            row.TestsTotal = total;
            row.TestsFailed = failed;
            await db.SaveChangesAsync(ct);
        }
        log.AppendLine().AppendLine($"=== all batches: {total} test(s), {failed} failing; batches with failures: {(failingBatches.Count == 0 ? "none" : string.Join(" | ", failingBatches))} ===");
        row.GateLogBlobUri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"assemblies/{row.Id:N}/gate.log", log.ToString(), "text/plain", ct);

        _bus.Publish(row.Id, Agent, "test", "stage", null, "Tests ran.");
        row.TestStatus = timedOutBatches > 0 ? "ERRORED" : failed == 0 && failingBatches.Count == 0 ? "PASSED" : "FAILED";
        if (row.TestStatus == "PASSED")
            await FinishAsync(db, row, "PASSED", $"One application from {row.UnitCount} code units: it builds and all {total} tests pass.", ct);
        else if (timedOutBatches > 0)
            await FinishAsync(db, row, "FAILED", $"The application builds; {failed} of {total} tests fail and {timedOutBatches} batch(es) of units did not finish in time. The log names them.", ct);
        else
            await FinishAsync(db, row, "FAILED", $"The application builds; {failed} of {total} tests fail together that passed unit by unit. The log names them.", ct);
    }

    private static readonly System.Text.RegularExpressions.Regex DotnetSummary = new(
        @"(?<verdict>Passed|Failed)!\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The .NET gate for an assembled application: `dotnet build` of the application, then `dotnet test` of every unit's
    /// tests together, in this container (the .NET SDK is in the API image), holding one gate slot for both.
    /// </summary>
    private async Task DotnetGateAsync(AppDbContext db, IBlobClient blob, StorageOptions storage, ModuleAssembly row, AssembledModule module, CancellationToken ct)
    {
        _bus.Publish(row.Id, Agent, "compile", "stage", null, "Waiting for a build slot…");
        using var slot = await GateExecutorPool.Shared.AcquireAsync(ct, ahead =>
        {
            _bus.Log(row.Id, Agent, "compile", $"Waiting for a build slot ({ahead} queued).");
            return Task.CompletedTask;
        });
        var dir = Path.Combine(Path.GetTempPath(), $"astra-assembly-{row.Id:N}");
        var log = new StringBuilder();
        try
        {
            foreach (var (path, content) in module.Files)
            {
                var abs = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, content, ct);
            }
            _bus.Publish(row.Id, Agent, "compile", "stage", null, "Building the assembled application…");
            var (buildExit, buildLog) = await Astra.Api.Validation.CompileValidator.RunDotnetBuildAsync(dir, ct);
            log.AppendLine(Tail(buildLog, 120_000));
            row.CompileStatus = buildExit == 0 ? "PASSED" : "FAILED";
            if (buildExit != 0)
            {
                var errors = System.Text.RegularExpressions.Regex.Matches(buildLog, @": error [A-Z]+\d+").Count;
                row.GateLogBlobUri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"assemblies/{row.Id:N}/gate.log", log.ToString(), "text/plain", ct);
                await FinishAsync(db, row, "FAILED", $"Assembled {row.UnitCount} code units, but the application does not build ({errors} compile error line(s)). The log says where.", ct);
                return;
            }
            if (!Directory.Exists(Path.Combine(dir, "tests")))
            {
                row.TestStatus = "PASSED";
                row.GateLogBlobUri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"assemblies/{row.Id:N}/gate.log", log.ToString(), "text/plain", ct);
                await FinishAsync(db, row, "PASSED", $"One application from {row.UnitCount} code units: it builds (no tests were carried).", ct);
                return;
            }
            _bus.Publish(row.Id, Agent, "test", "stage", null, "Running every unit's tests together…");
            var (testExit, testLog) = await Astra.Api.Validation.TestPackValidator.RunDotnetTestAsync(dir, ct);
            log.AppendLine(Tail(testLog, 200_000));
            int total = 0, failed = 0, skipped = 0;
            foreach (System.Text.RegularExpressions.Match m in DotnetSummary.Matches(testLog))
            {
                total += int.Parse(m.Groups["total"].Value);
                failed += int.Parse(m.Groups["failed"].Value);
                skipped += int.Parse(m.Groups["skipped"].Value);
            }
            row.TestsTotal = total;
            row.TestsFailed = failed;
            row.GateLogBlobUri = await blob.PutTextAsync(storage.Buckets.Scaffolds, $"assemblies/{row.Id:N}/gate.log", log.ToString(), "text/plain", ct);
            row.TestStatus = total == 0 && testExit != 0 ? "ERRORED" : failed == 0 && testExit == 0 ? "PASSED" : "FAILED";
            var skippedNote = skipped > 0 ? $" ({skipped} skipped: they need code whose source is not in the project)" : "";
            if (row.TestStatus == "PASSED")
                await FinishAsync(db, row, "PASSED", $"One application from {row.UnitCount} code units: it builds and all {total - skipped} runnable tests pass{skippedNote}.", ct);
            else if (row.TestStatus == "ERRORED")
                await FinishAsync(db, row, "FAILED", "The application builds, but its tests did not run (the tests project does not build). The log says why.", ct);
            else
                await FinishAsync(db, row, "FAILED", $"The application builds; {failed} of {total} tests fail together{skippedNote}. The log names them.", ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Units tested per request: small enough that a batch of Spring contexts finishes well inside the limit.</summary>
    public const int UnitsPerTestBatch = 6;
    private const int BatchTimeoutMs = 200_000;

    /// <summary>The units' packages in batches of <paramref name="size"/>, in order.</summary>
    public static List<List<string>> TestBatches(IReadOnlyList<string> packages, int size) =>
        packages.Select((p, i) => (p, i)).GroupBy(x => x.i / Math.Max(1, size)).Select(g => g.Select(x => x.p).ToList()).ToList();

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private async Task FinishAsync(AppDbContext db, ModuleAssembly row, string state, string summary, CancellationToken ct)
    {
        row.State = state;
        row.Summary = summary;
        row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _bus.State(row.Id, Agent, state == "PASSED" ? "SUCCEEDED" : "FAILED", summary);
    }

    /// <summary>The assembly as the API and the card show it.</summary>
    public static object Shape(ModuleAssembly a)
    {
        JsonElement? report = null;
        if (a.ReportJson is { Length: > 0 } rj)
            try { report = JsonDocument.Parse(rj).RootElement.Clone(); } catch (JsonException) { }
        return new
        {
            id = a.Id,
            corpusId = a.CorpusId,
            name = a.Name,
            targetStack = a.TargetStack,
            state = a.State,
            summary = a.Summary,
            unitCount = a.UnitCount,
            excludedCount = a.ExcludedCount,
            fileCount = a.FileCount,
            compileStatus = a.CompileStatus,
            testStatus = a.TestStatus,
            testsTotal = a.TestsTotal,
            testsFailed = a.TestsFailed,
            createdBy = a.CreatedBy,
            createdAt = a.CreatedAt,
            completedAt = a.CompletedAt,
            downloadUrl = a.PackageBlobUri is null ? null : $"/api/v1/assemblies/{a.Id}/package.zip",
            report,
        };
    }
}
