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
/// Stage-2 validation: materialise the scaffold (which by this point
/// includes the auto-generated SignedSpecPack) and run <c>dotnet test</c>.
/// Captures the test runner output to a MinIO blob, parses pass/fail/skip
/// counts out of the standard <c>Passed!</c>/<c>Failed!</c> summary line,
/// and writes a <see cref="ValidationRun"/> row with stage = TEST_PACK.
///
/// Test-pack failures don't crash the validator — they're a normal output
/// state. The commit-blocker in <c>ScaffoldEndpoints</c> uses the PASSED
/// status here as the gate.
/// </summary>
public sealed class TestPackValidator
{
    // Two known dotnet-test summary formats:
    //   A) Single-line:  "Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11, Duration: 3 s"
    //                    (older "console-logger" output, still used in some 6.0 builds)
    //   B) Multi-line:   "Test Run Successful." / "Total tests: N" / "     Passed: N"
    //                    (default vstest output on .NET 8 SDK 8.0.421)
    private static readonly Regex SummaryLineSingle = new(
        @"^(?<verdict>Passed|Failed)!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex VerdictMulti = new(
        @"^Test Run (?<verdict>Successful|Failed)\.\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TotalMulti = new(
        @"^Total tests:\s*(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PassedMulti = new(
        @"^\s+Passed:\s*(?<passed>\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex FailedMulti = new(
        @"^\s+Failed:\s*(?<failed>\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex SkippedMulti = new(
        @"^\s+Skipped:\s*(?<skipped>\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TestPackValidator> _log;

    public TestPackValidator(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        ILogger<TestPackValidator> log)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _log = log;
    }

    public async Task<ValidationRun> RunAsync(
        Guid scaffoldId,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var scaffold = await _db.Scaffolds.FirstOrDefaultAsync(s => s.Id == scaffoldId, ct)
            ?? throw new InvalidOperationException($"Scaffold {scaffoldId} not found.");

        var run = new ValidationRun
        {
            Id = Guid.NewGuid(),
            ScaffoldId = scaffold.Id,
            SpecId = scaffold.SpecId,
            Stage = "TEST_PACK",
            Status = "RUNNING",
            Summary = "Test pack queued",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.ValidationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "validation.started", "scaffold", scaffold.Id, actor,
            payload: new { runId = run.Id, stage = run.Stage },
            ct: ct);

        var tempDir = Path.Combine(Path.GetTempPath(), $"astra-testpack-{run.Id:N}");
        try
        {
            // 1. Materialise the scaffold (including the generated SignedSpecPack).
            var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
            using var manifest = JsonDocument.Parse(manifestText);
            var files = manifest.RootElement.GetProperty("files");

            Directory.CreateDirectory(tempDir);
            int fileCount = 0;
            foreach (var file in files.EnumerateArray())
            {
                var relPath = file.GetProperty("path").GetString()!;
                var content = file.GetProperty("content").GetString() ?? "";
                var abs = Path.Combine(tempDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, content, ct);
                fileCount++;
            }

            // 2. Run `dotnet test`. We target the tests/ project explicitly
            //    so the runner doesn't try to also run src/ as a test project.
            var (exitCode, log) = await RunDotnetTestAsync(tempDir, ct);

            // 3. Parse the summary out of the log. Try the single-line format
            //    first; fall through to the multi-line format on .NET 8 SDK.
            int passed = 0, failed = 0, skipped = 0, total = 0;
            string verdict = "Unknown";
            var singleMatches = SummaryLineSingle.Matches(log);
            if (singleMatches.Count > 0)
            {
                foreach (Match m in singleMatches)
                {
                    if (verdict == "Unknown") verdict = m.Groups["verdict"].Value;
                    else if (m.Groups["verdict"].Value == "Failed") verdict = "Failed";
                    failed += int.Parse(m.Groups["failed"].Value);
                    passed += int.Parse(m.Groups["passed"].Value);
                    skipped += int.Parse(m.Groups["skipped"].Value);
                    total += int.Parse(m.Groups["total"].Value);
                }
            }
            else
            {
                // Multi-line. There can be multiple "Total tests:" sections
                // (one per assembly); sum across all of them.
                foreach (Match m in TotalMulti.Matches(log))   total += int.Parse(m.Groups["total"].Value);
                foreach (Match m in PassedMulti.Matches(log))  passed += int.Parse(m.Groups["passed"].Value);
                foreach (Match m in FailedMulti.Matches(log))  failed += int.Parse(m.Groups["failed"].Value);
                foreach (Match m in SkippedMulti.Matches(log)) skipped += int.Parse(m.Groups["skipped"].Value);
                var v = VerdictMulti.Matches(log);
                if (v.Count > 0)
                {
                    verdict = v.Cast<Match>().Any(m => m.Groups["verdict"].Value == "Failed") ? "Failed" : "Passed";
                }
            }

            // 4. Upload the runner log to MinIO.
            var logKey = $"validation/{run.Id:N}/test-pack.log";
            var logUri = await _blob.PutTextAsync(
                _storage.Buckets.Scaffolds, logKey, log, "text/plain", ct);

            // 5. Persist verdict + metrics.
            run.LogBlobUri = logUri;
            run.MetricsJson = JsonSerializer.Serialize(new
            {
                exitCode,
                verdict,
                passed,
                failed,
                skipped,
                total,
                fileCount,
            });
            run.CompletedAt = DateTimeOffset.UtcNow;

            if (total == 0)
            {
                // No tests detected at all — could be a build failure or a
                // missing test project. Treat as ERRORED so the report-card
                // page differentiates from "ran but every test failed".
                run.Status = "ERRORED";
                run.ErrorCode = "test_pack.no_tests_detected";
                run.Summary = exitCode == 0
                    ? "No tests detected"
                    : $"dotnet test exited {exitCode}; no test summary parsed";
            }
            else if (failed == 0 && exitCode == 0)
            {
                run.Status = "PASSED";
                run.Summary = skipped == 0
                    ? $"All {total} tests passed"
                    : $"{passed}/{total} passed · {skipped} skipped";
            }
            else
            {
                run.Status = "FAILED";
                run.ErrorCode = "test_pack.tests_failed";
                run.Summary = $"{failed} of {total} tests failed · {passed} passed · {skipped} skipped";
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
                "Test pack validation for scaffold {Scaffold}: {Status} ({Summary})",
                scaffold.Id, run.Status, run.Summary);

            return run;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Test pack validation crashed for scaffold {Scaffold}", scaffold.Id);
            run.Status = "ERRORED";
            run.ErrorCode = "test_pack.runner_crashed";
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
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to clean up {Dir}", tempDir); }
        }
    }

    private static async Task<(int ExitCode, string Log)> RunDotnetTestAsync(
        string workDir, CancellationToken ct)
    {
        // `dotnet test` without a project arg walks the cwd for the FIRST
        // .csproj it finds, which is the src project — that has no tests
        // and reports "Total: 0". Point at tests/ explicitly. If the
        // scaffold doesn't follow the tests/ convention, the validator
        // surfaces "no tests detected" with exit 0 which the caller can
        // see in the metrics.
        var testsDir = Path.Combine(workDir, "tests");
        var arg = Directory.Exists(testsDir) ? "test tests --nologo --verbosity normal" : "test --nologo --verbosity normal";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arg,
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
            throw new InvalidOperationException("dotnet test failed to start.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"dotnet test did not finish within {TestTimeout.TotalSeconds}s.");
        }

        var combined = new StringBuilder();
        combined.AppendLine($"=== dotnet test (cwd={workDir}) ===");
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
