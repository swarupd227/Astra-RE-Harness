using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Stage-1 validation: materialise the scaffold to a temp directory and
/// run <c>dotnet build</c> against it. Captures stdout/stderr to a MinIO
/// blob, parses warnings/errors out of the log, and writes a
/// <see cref="ValidationRun"/> row with status + summary + metrics.
///
/// In-process for now — the scaffolds are fully canonical (no untrusted
/// codegen yet) so sandbox isolation is overkill. When Phase D opens
/// the generation path to free-form LLM output, swap the Process.Start
/// call for a docker-run against a throwaway sidecar.
/// </summary>
public sealed class CompileValidator
{
    private static readonly Regex ErrorLine = new(@"\berror\s+[A-Z]+\d+:", RegexOptions.Compiled);
    private static readonly Regex WarningLine = new(@"\bwarning\s+[A-Z]+\d+:", RegexOptions.Compiled);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly MavenClient _maven;
    private readonly ILogger<CompileValidator> _log;

    public CompileValidator(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        MavenClient maven,
        ILogger<CompileValidator> log)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _maven = maven;
        _log = log;
    }

    public async Task<ValidationRun> RunAsync(
        Guid scaffoldId,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var scaffold = await _db.Scaffolds
            .FirstOrDefaultAsync(s => s.Id == scaffoldId, ct)
            ?? throw new InvalidOperationException($"Scaffold {scaffoldId} not found.");

        var run = new ValidationRun
        {
            Id = Guid.NewGuid(),
            ScaffoldId = scaffold.Id,
            SpecId = scaffold.SpecId,
            Stage = "COMPILE",
            Status = "RUNNING",
            Summary = "Build queued",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.ValidationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "validation.started", "scaffold", scaffold.Id, actor,
            payload: new { runId = run.Id, stage = run.Stage, targetPlatform = scaffold.TargetPlatform },
            ct: ct);

        try
        {
            // Phase 5.5: dispatch by target platform. The dotnet8 path
            // materialises the scaffold and shells out locally; the
            // java-spring path POSTs the sources to the maven sidecar so
            // mvn runs inside the pre-warmed image cache.
            if (string.Equals(scaffold.TargetPlatform, "java-spring", StringComparison.OrdinalIgnoreCase))
            {
                await RunJavaCompileAsync(scaffold, run, actor, ct);
            }
            else
            {
                await RunDotnetCompileAsync(scaffold, run, actor, ct);
            }
            return run;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Compile validation crashed for scaffold {Scaffold}", scaffold.Id);
            run.Status = "ERRORED";
            run.ErrorCode = "compile.runner_crashed";
            run.Summary = $"Runner error: {ex.GetType().Name}: {ex.Message}";
            run.CompletedAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception saveEx) { _log.LogError(saveEx, "Could not persist ERRORED state"); }

            await _audit.LogAsync(
                "validation.completed", "scaffold", scaffold.Id, actor,
                payload: new { runId = run.Id, stage = run.Stage, status = run.Status, error = ex.Message },
                ct: ct);
            return run;
        }
    }

    private async Task RunDotnetCompileAsync(
        Persistence.Entities.Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"astra-validate-{run.Id:N}");
        try
        {
            // 1. Materialise the scaffold to a temp directory.
            var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
            using var manifest = JsonDocument.Parse(manifestText);
            var files = manifest.RootElement.GetProperty("files");

            Directory.CreateDirectory(tempDir);
            int fileCount = 0;
            foreach (var file in files.EnumerateArray())
            {
                var relPath = file.GetProperty("path").GetString()
                    ?? throw new InvalidOperationException("Manifest file entry missing 'path'.");
                var content = file.GetProperty("content").GetString() ?? "";
                var abs = Path.Combine(tempDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, content, ct);
                fileCount++;
            }
            _log.LogInformation(
                "Materialised {Files} files for scaffold {Scaffold} at {Dir}",
                fileCount, scaffold.Id, tempDir);

            // 2. Run `dotnet build` capturing stdout + stderr.
            var (exitCode, log) = await RunDotnetBuildAsync(tempDir, ct);

            // 3. Parse warnings + errors out of the build log.
            int errorCount = 0, warningCount = 0;
            foreach (var line in log.Split('\n'))
            {
                if (ErrorLine.IsMatch(line)) errorCount++;
                else if (WarningLine.IsMatch(line)) warningCount++;
            }

            // 4. Upload the build log to MinIO for posterity.
            var logKey = $"validation/{run.Id:N}/compile.log";
            var logUri = await _blob.PutTextAsync(
                _storage.Buckets.Scaffolds, logKey, log, "text/plain", ct);

            // 5. Update the ValidationRun row with the verdict.
            run.LogBlobUri = logUri;
            run.MetricsJson = JsonSerializer.Serialize(new
            {
                runner = "dotnet",
                exitCode,
                errorCount,
                warningCount,
                fileCount,
                logLines = log.Count(c => c == '\n'),
            });
            run.CompletedAt = DateTimeOffset.UtcNow;

            if (exitCode == 0)
            {
                run.Status = "PASSED";
                run.Summary = warningCount == 0
                    ? "Build succeeded · 0 warnings"
                    : $"Build succeeded · {warningCount} warning{(warningCount == 1 ? "" : "s")}";
            }
            else
            {
                run.Status = "FAILED";
                run.ErrorCode = "compile.build_failed";
                run.Summary = errorCount > 0
                    ? $"Build failed · {errorCount} error{(errorCount == 1 ? "" : "s")} · {warningCount} warning{(warningCount == 1 ? "" : "s")}"
                    : $"Build failed · exit {exitCode}";
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(
                "validation.completed", "scaffold", scaffold.Id, actor,
                payload: new
                {
                    runId = run.Id,
                    stage = run.Stage,
                    status = run.Status,
                    summary = run.Summary,
                    metrics = JsonDocument.Parse(run.MetricsJson),
                },
                ct: ct);

            _log.LogInformation(
                "Compile validation for scaffold {Scaffold}: {Status} ({Summary})",
                scaffold.Id, run.Status, run.Summary);
        }
        finally
        {
            // Clean up the materialised files. Keep the obj/ + bin/ folders
            // out of the way — they live entirely inside tempDir and get
            // wiped along with everything else.
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to clean up {Dir}", tempDir);
            }
        }
    }

    private async Task RunJavaCompileAsync(
        Persistence.Entities.Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        // 1. Pull the file manifest from MinIO.
        var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
        using var manifest = JsonDocument.Parse(manifestText);
        var files = manifest.RootElement.GetProperty("files");

        // 2. Forward every file (pom.xml, sources, tests, resources) to the
        //    maven sidecar. The sidecar materialises them into its workdir
        //    so mvn can hit the warmed-up local Maven repo.
        var sources = new List<MavenClient.JavaSource>();
        foreach (var file in files.EnumerateArray())
        {
            var relPath = file.GetProperty("path").GetString()
                ?? throw new InvalidOperationException("Manifest file entry missing 'path'.");
            var content = file.GetProperty("content").GetString() ?? "";
            sources.Add(new MavenClient.JavaSource(relPath, content));
        }

        if (!await _maven.PingAsync(ct))
            throw new InvalidOperationException("maven sidecar unreachable (GET /health failed).");

        _log.LogInformation(
            "Forwarding {Files} files for scaffold {Scaffold} to maven sidecar",
            sources.Count, scaffold.Id);

        // 3. POST /compile — maven sidecar shells out to `mvn -o test-compile`
        //    and parses its own [WARNING]/[ERROR] markers.
        var summary = await _maven.CompileAsync(
            new MavenClient.CompileRequest(sources), ct);

        // 4. Persist the full sidecar log to MinIO so the report card UI
        //    can render it next to the dotnet log.
        var logKey = $"validation/{run.Id:N}/compile.log";
        var logUri = await _blob.PutTextAsync(
            _storage.Buckets.Scaffolds, logKey, summary.Log, "text/plain", ct);

        run.LogBlobUri = logUri;
        run.MetricsJson = JsonSerializer.Serialize(new
        {
            runner = "maven",
            artifactId = summary.ArtifactId,
            exitCode = summary.ExitCode,
            errorCount = summary.ErrorCount,
            warningCount = summary.WarningCount,
            fileCount = sources.Count,
            durationMs = summary.DurationMs,
            logLines = summary.Log.Count(c => c == '\n'),
        });
        run.CompletedAt = DateTimeOffset.UtcNow;

        if (summary.ExitCode == 0)
        {
            run.Status = "PASSED";
            run.Summary = summary.WarningCount == 0
                ? $"mvn compile succeeded · 0 warnings · {summary.DurationMs} ms"
                : $"mvn compile succeeded · {summary.WarningCount} warning{(summary.WarningCount == 1 ? "" : "s")} · {summary.DurationMs} ms";
        }
        else
        {
            run.Status = "FAILED";
            run.ErrorCode = "compile.build_failed";
            run.Summary = summary.ErrorCount > 0
                ? $"mvn compile failed · {summary.ErrorCount} error{(summary.ErrorCount == 1 ? "" : "s")} · {summary.WarningCount} warning{(summary.WarningCount == 1 ? "" : "s")}"
                : $"mvn compile failed · exit {summary.ExitCode}";
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "validation.completed", "scaffold", scaffold.Id, actor,
            payload: new
            {
                runId = run.Id,
                stage = run.Stage,
                status = run.Status,
                summary = run.Summary,
                metrics = JsonDocument.Parse(run.MetricsJson),
            },
            ct: ct);

        _log.LogInformation(
            "Java compile validation for scaffold {Scaffold}: {Status} ({Summary})",
            scaffold.Id, run.Status, run.Summary);
    }

    private static async Task<(int ExitCode, string Log)> RunDotnetBuildAsync(
        string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // No restore-disable: we want NuGet to pull xunit + Moq for the
            // tests project. The first run is slow (~30s); subsequent runs
            // are fast against the warm package cache.
            Arguments = "build --nologo --verbosity normal",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException("dotnet build failed to start.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(BuildTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"dotnet build did not finish within {BuildTimeout.TotalSeconds}s.");
        }

        var combined = new StringBuilder();
        combined.AppendLine($"=== dotnet build (cwd={workDir}) ===");
        combined.AppendLine(stdout.ToString());
        if (stderr.Length > 0)
        {
            combined.AppendLine("=== stderr ===");
            combined.Append(stderr);
        }
        combined.AppendLine($"=== exit {proc.ExitCode} ===");

        return (proc.ExitCode, combined.ToString());
    }
}
