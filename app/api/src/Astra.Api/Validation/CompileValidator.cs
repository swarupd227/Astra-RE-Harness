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
    // npm install can legitimately take longer than a dotnet build on a
    // cold cache — this is only exercised by angular-dotnet8.
    private static readonly TimeSpan NpmTimeout = TimeSpan.FromMinutes(8);

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
            // mvn runs inside the pre-warmed image cache. angular-dotnet8
            // (the first two-runtime target stack) builds both halves
            // in-process and reports one combined verdict. angular-java
            // is the second two-runtime stack — same combined-verdict
            // shape, but the backend half goes to the maven sidecar
            // (like java-spring) instead of running in-process.
            if (string.Equals(scaffold.TargetPlatform, "java-spring", StringComparison.OrdinalIgnoreCase))
            {
                await RunJavaCompileAsync(scaffold, run, actor, ct);
            }
            else if (string.Equals(scaffold.TargetPlatform, "angular-dotnet8", StringComparison.OrdinalIgnoreCase))
            {
                await RunAngularDotnetCompileAsync(scaffold, run, actor, ct);
            }
            else if (string.Equals(scaffold.TargetPlatform, "angular-java", StringComparison.OrdinalIgnoreCase))
            {
                await RunAngularJavaCompileAsync(scaffold, run, actor, ct);
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

    /// <summary>
    /// angular-dotnet8: the first target stack spanning two runtimes in one
    /// scaffold. Builds backend/ with `dotnet build` (reusing the exact
    /// helper the dotnet8-only path uses, just pointed at the subdirectory)
    /// and frontend/ with `npm install` + `npm run build` (Angular CLI's
    /// production build), then reports one combined PASSED/FAILED verdict.
    /// The two builds are independent — a broken frontend doesn't hide a
    /// broken backend or vice versa; both exit codes are in the metrics.
    /// </summary>
    private async Task RunAngularDotnetCompileAsync(
        Persistence.Entities.Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"astra-validate-{run.Id:N}");
        try
        {
            // 1. Materialise the scaffold. Paths are already rooted at
            //    backend/ or frontend/ per the archetype's own layout.
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
                "Materialised {Files} files for angular-dotnet8 scaffold {Scaffold} at {Dir}",
                fileCount, scaffold.Id, tempDir);

            var backendDir = Path.Combine(tempDir, "backend");
            var frontendDir = Path.Combine(tempDir, "frontend");

            // 2. Build both halves with their own toolchain.
            var (dotnetExit, dotnetLog) = Directory.Exists(backendDir)
                ? await RunDotnetBuildAsync(backendDir, ct)
                : (-1, "=== backend/ directory missing from scaffold ===\n");

            var (npmExit, npmLog) = Directory.Exists(frontendDir)
                ? await RunNpmBuildAsync(frontendDir, ct)
                : (-1, "=== frontend/ directory missing from scaffold ===\n");

            // 3. Parse warnings/errors out of the dotnet log the same way
            //    the dotnet8-only path does. ng build's error format doesn't
            //    match those regexes and isn't worth a second parser for a
            //    count that's redundant with the exit code either way.
            int errorCount = 0, warningCount = 0;
            foreach (var line in dotnetLog.Split('\n'))
            {
                if (ErrorLine.IsMatch(line)) errorCount++;
                else if (WarningLine.IsMatch(line)) warningCount++;
            }

            var combinedLog = new StringBuilder();
            combinedLog.AppendLine("=== backend (dotnet build) ===");
            combinedLog.AppendLine(dotnetLog);
            combinedLog.AppendLine();
            combinedLog.AppendLine("=== frontend (npm install + ng build) ===");
            combinedLog.AppendLine(npmLog);
            var combinedLogText = combinedLog.ToString();

            // 4. Upload the combined build log to MinIO for posterity.
            var logKey = $"validation/{run.Id:N}/compile.log";
            var logUri = await _blob.PutTextAsync(
                _storage.Buckets.Scaffolds, logKey, combinedLogText, "text/plain", ct);

            // 5. Update the ValidationRun row with the verdict.
            run.LogBlobUri = logUri;
            run.MetricsJson = JsonSerializer.Serialize(new
            {
                runner = "dotnet+npm",
                backendExitCode = dotnetExit,
                frontendExitCode = npmExit,
                errorCount,
                warningCount,
                fileCount,
                logLines = combinedLogText.Count(c => c == '\n'),
            });
            run.CompletedAt = DateTimeOffset.UtcNow;

            if (dotnetExit == 0 && npmExit == 0)
            {
                run.Status = "PASSED";
                run.Summary = warningCount == 0
                    ? "Backend + frontend build succeeded · 0 dotnet warnings"
                    : $"Backend + frontend build succeeded · {warningCount} dotnet warning{(warningCount == 1 ? "" : "s")}";
            }
            else
            {
                run.Status = "FAILED";
                run.ErrorCode = "compile.build_failed";
                var parts = new List<string>();
                if (dotnetExit != 0) parts.Add($"backend exit {dotnetExit}");
                if (npmExit != 0) parts.Add($"frontend exit {npmExit}");
                run.Summary = $"Build failed · {string.Join(", ", parts)}";
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
                "Angular+dotnet compile validation for scaffold {Scaffold}: {Status} ({Summary})",
                scaffold.Id, run.Status, run.Summary);
        }
        finally
        {
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

    /// <summary>
    /// angular-java: the second two-runtime target stack. Builds frontend/
    /// the same way angular-dotnet8 does (`npm install` + `ng build`,
    /// in-process) but the backend/ half goes to the maven sidecar exactly
    /// like java-spring's RunJavaCompileAsync — with the `backend/` prefix
    /// stripped off every path first, since the sidecar expects `pom.xml`
    /// and `src/main/java/...` at its OWN workdir root, not nested under a
    /// subdirectory.
    /// </summary>
    private async Task RunAngularJavaCompileAsync(
        Persistence.Entities.Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"astra-validate-{run.Id:N}");
        try
        {
            var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
            using var manifest = JsonDocument.Parse(manifestText);
            var files = manifest.RootElement.GetProperty("files");

            Directory.CreateDirectory(tempDir);
            var mavenSources = new List<MavenClient.JavaSource>();
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

                const string backendPrefix = "backend/";
                if (relPath.StartsWith(backendPrefix, StringComparison.Ordinal))
                    mavenSources.Add(new MavenClient.JavaSource(relPath[backendPrefix.Length..], content));
            }
            _log.LogInformation(
                "Materialised {Files} files for angular-java scaffold {Scaffold} at {Dir}",
                fileCount, scaffold.Id, tempDir);

            var frontendDir = Path.Combine(tempDir, "frontend");
            var (npmExit, npmLog) = Directory.Exists(frontendDir)
                ? await RunNpmBuildAsync(frontendDir, ct)
                : (-1, "=== frontend/ directory missing from scaffold ===\n");

            MavenClient.CompileSummary? mavenSummary = null;
            string mavenLog;
            if (mavenSources.Count == 0)
            {
                mavenLog = "=== backend/ directory missing from scaffold ===\n";
            }
            else if (!await _maven.PingAsync(ct))
            {
                mavenLog = "=== maven sidecar unreachable (GET /health failed) ===\n";
            }
            else
            {
                mavenSummary = await _maven.CompileAsync(new MavenClient.CompileRequest(mavenSources), ct);
                mavenLog = mavenSummary.Log;
            }

            var combinedLog = new StringBuilder();
            combinedLog.AppendLine("=== backend (mvn -o test-compile via maven sidecar) ===");
            combinedLog.AppendLine(mavenLog);
            combinedLog.AppendLine();
            combinedLog.AppendLine("=== frontend (npm install + ng build) ===");
            combinedLog.AppendLine(npmLog);
            var combinedLogText = combinedLog.ToString();

            var logKey = $"validation/{run.Id:N}/compile.log";
            var logUri = await _blob.PutTextAsync(
                _storage.Buckets.Scaffolds, logKey, combinedLogText, "text/plain", ct);

            var backendExit = mavenSummary?.ExitCode ?? -1;
            run.LogBlobUri = logUri;
            run.MetricsJson = JsonSerializer.Serialize(new
            {
                runner = "maven+npm",
                backendExitCode = backendExit,
                frontendExitCode = npmExit,
                backendErrorCount = mavenSummary?.ErrorCount ?? 0,
                backendWarningCount = mavenSummary?.WarningCount ?? 0,
                fileCount,
                logLines = combinedLogText.Count(c => c == '\n'),
            });
            run.CompletedAt = DateTimeOffset.UtcNow;

            if (backendExit == 0 && npmExit == 0)
            {
                run.Status = "PASSED";
                var warnings = mavenSummary!.WarningCount;
                run.Summary = warnings == 0
                    ? "Backend + frontend build succeeded · 0 mvn warnings"
                    : $"Backend + frontend build succeeded · {warnings} mvn warning{(warnings == 1 ? "" : "s")}";
            }
            else
            {
                run.Status = "FAILED";
                run.ErrorCode = "compile.build_failed";
                var parts = new List<string>();
                if (backendExit != 0) parts.Add($"backend exit {backendExit}");
                if (npmExit != 0) parts.Add($"frontend exit {npmExit}");
                run.Summary = $"Build failed · {string.Join(", ", parts)}";
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
                "Angular+Java compile validation for scaffold {Scaffold}: {Status} ({Summary})",
                scaffold.Id, run.Status, run.Summary);
        }
        finally
        {
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

    /// <summary>
    /// `npm install` then `npm run build` (Angular CLI's production build,
    /// per the archetype's package.json script). No package-lock.json ships
    /// in the scaffold — a real Angular lockfile runs to hundreds of KB, far
    /// past what an LLM can reasonably regenerate per routine inside a
    /// 16K-token response — so `npm ci` (which requires an existing
    /// lockfile) isn't an option; `npm install` resolves fresh from
    /// package.json's version ranges instead. Less reproducible than a
    /// committed lockfile, but the only viable choice given that constraint.
    /// </summary>
    private static async Task<(int ExitCode, string Log)> RunNpmBuildAsync(
        string workDir, CancellationToken ct)
    {
        var (installExit, installLog) = await RunNpmAsync(workDir, "install", ct);
        var combined = new StringBuilder();
        combined.AppendLine($"=== npm install (cwd={workDir}) ===");
        combined.AppendLine(installLog);
        if (installExit != 0)
            return (installExit, combined.ToString());

        var (buildExit, buildLog) = await RunNpmAsync(workDir, "run build", ct);
        combined.AppendLine($"=== npm run build (cwd={workDir}) ===");
        combined.AppendLine(buildLog);
        return (buildExit, combined.ToString());
    }

    private static async Task<(int ExitCode, string Log)> RunNpmAsync(
        string workDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
            Arguments = arguments,
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
            throw new InvalidOperationException($"npm {arguments} failed to start.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(NpmTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"npm {arguments} did not finish within {NpmTimeout.TotalSeconds}s.");
        }

        var combined = new StringBuilder();
        combined.Append(stdout);
        if (stderr.Length > 0)
        {
            combined.AppendLine("--- stderr ---");
            combined.Append(stderr);
        }
        combined.AppendLine($"=== exit {proc.ExitCode} ===");
        return (proc.ExitCode, combined.ToString());
    }
}
