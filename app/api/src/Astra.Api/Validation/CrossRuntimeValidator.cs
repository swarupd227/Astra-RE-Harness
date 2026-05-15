using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Stage-3 validation: drive the gfortran reference binary and the
/// generated C# scaffold with the same input vector and assert their
/// outputs match within tolerance.
///
/// For the demo cut, the validator runs a single "smoke" equivalence
/// case: a trivial integer-sum routine that proves the cross-runtime
/// loop is wired correctly (gfortran sidecar reachable, JSON in / JSON
/// out, both runtimes return the same value). Per-routine equivalence
/// against the real signed scaffold lives behind the same surface — we
/// just need (a) ISAM/IO shim stubs in the Fortran payload so the
/// reference binary links, and (b) the engineer's C# implementation
/// to be non-stubby. Until both land, this runs the smoke case and
/// records that explicitly in the run's metrics + summary so the
/// report-card surface (#2d) shows AMBER (smoke passes, real equivalence
/// pending) rather than a dishonest green.
/// </summary>
public sealed class CrossRuntimeValidator
{
    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly GfortranClient _gfortran;
    private readonly ILogger<CrossRuntimeValidator> _log;

    public CrossRuntimeValidator(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        GfortranClient gfortran,
        ILogger<CrossRuntimeValidator> log)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _gfortran = gfortran;
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
            Stage = "EQUIVALENCE",
            Status = "RUNNING",
            Summary = "Cross-runtime equivalence check queued",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.ValidationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "validation.started", "scaffold", scaffold.Id, actor,
            payload: new { runId = run.Id, stage = run.Stage },
            ct: ct);

        try
        {
            // ─── Sanity: is the gfortran sidecar reachable? ────────────────
            if (!await _gfortran.PingAsync(ct))
                throw new InvalidOperationException(
                    "gfortran sidecar health probe failed (Validation:GfortranEndpoint).");

            // ─── Smoke case (canonical, baked in) ──────────────────────────
            // A trivial Fortran routine that reads two integers from stdin,
            // adds them, and prints {"sum": N} to stdout. The C# reference
            // computes the same locally. Both must agree.
            const string smokeSource = """
                program smoke
                  implicit none
                  integer :: a, b, sum
                  read(*,*) a, b
                  sum = a + b
                  write(*,'("{""sum"":",I0,"}")') sum
                end program smoke
                """;
            const string stdinPayload = "3 4\n";
            const int expectedSum = 3 + 4;

            var fortran = await _gfortran.CompileAndRunAsync(new GfortranClient.CompileAndRunRequest(
                Sources: new[] { new GfortranClient.Source("smoke.f90", smokeSource) },
                Stdin: stdinPayload,
                TimeoutMs: 30_000), ct);

            // Parse the Fortran-side output.
            int fortranSum;
            try
            {
                using var doc = JsonDocument.Parse((fortran.Run?.Stdout ?? "").Trim());
                fortranSum = doc.RootElement.GetProperty("sum").GetInt32();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse gfortran stdout as JSON: {ex.Message}; raw: {fortran.Run?.Stdout}");
            }

            var match = fortranSum == expectedSum;

            // ─── Persist the build log to MinIO ─────────────────────────────
            var transcript = new System.Text.StringBuilder();
            transcript.AppendLine("=== gfortran sidecar — compile ===");
            transcript.AppendLine($"exit {fortran.Compile.ExitCode} · {fortran.Compile.DurationMs}ms · {fortran.Compile.ErrorCount} errors · {fortran.Compile.WarningCount} warnings");
            transcript.AppendLine(fortran.Compile.Log);
            transcript.AppendLine("=== gfortran sidecar — run ===");
            transcript.AppendLine($"exit {fortran.Run?.ExitCode ?? -1} · {fortran.Run?.DurationMs ?? -1}ms · timedOut={fortran.Run?.TimedOut ?? false}");
            transcript.AppendLine($"stdin: {stdinPayload.Replace("\n", @"\n")}");
            transcript.AppendLine($"stdout: {fortran.Run?.Stdout}");
            if (!string.IsNullOrWhiteSpace(fortran.Run?.Stderr))
                transcript.AppendLine($"stderr: {fortran.Run.Stderr}");
            transcript.AppendLine();
            transcript.AppendLine("=== C# reference ===");
            transcript.AppendLine($"computed: {expectedSum}");
            transcript.AppendLine();
            transcript.AppendLine($"=== equivalence verdict ===");
            transcript.AppendLine($"fortran_sum={fortranSum} csharp_sum={expectedSum} match={match}");

            var logKey = $"validation/{run.Id:N}/equivalence.log";
            var logUri = await _blob.PutTextAsync(
                _storage.Buckets.Scaffolds, logKey, transcript.ToString(), "text/plain", ct);

            // ─── Persist verdict ───────────────────────────────────────────
            run.LogBlobUri = logUri;
            run.MetricsJson = JsonSerializer.Serialize(new
            {
                mode = "smoke",
                fortranCompileExit = fortran.Compile.ExitCode,
                fortranCompileMs = fortran.Compile.DurationMs,
                fortranRunExit = fortran.Run?.ExitCode ?? -1,
                fortranRunMs = fortran.Run?.DurationMs ?? -1,
                fortranSum,
                csharpSum = expectedSum,
                match,
            });
            run.CompletedAt = DateTimeOffset.UtcNow;

            if (match && fortran.Compile.ExitCode == 0 && (fortran.Run?.ExitCode ?? -1) == 0)
            {
                run.Status = "PASSED";
                run.Summary = "Smoke equivalence: gfortran and C# agree on canonical input. " +
                              "Per-routine equivalence runs through the same loop when ISAM " +
                              "shims + C# implementation land.";
            }
            else
            {
                run.Status = "FAILED";
                run.ErrorCode = match ? "equivalence.runtime_error" : "equivalence.outputs_differ";
                run.Summary = match
                    ? $"Gfortran exit codes non-zero (compile={fortran.Compile.ExitCode}, run={fortran.Run?.ExitCode})."
                    : $"Outputs differ: fortran={fortranSum} vs csharp={expectedSum}";
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
                "Cross-runtime validation for scaffold {Scaffold}: {Status} ({Summary})",
                scaffold.Id, run.Status, run.Summary);
            return run;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cross-runtime validation crashed for scaffold {Scaffold}", scaffold.Id);
            run.Status = "ERRORED";
            run.ErrorCode = "equivalence.runner_crashed";
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
}
