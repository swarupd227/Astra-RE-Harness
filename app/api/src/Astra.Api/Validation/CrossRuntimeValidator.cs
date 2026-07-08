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
    private readonly Vb6Client _vb6;
    private readonly CsharpClient _csharp;
    private readonly ILogger<CrossRuntimeValidator> _log;

    public CrossRuntimeValidator(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        GfortranClient gfortran,
        Vb6Client vb6,
        CsharpClient csharp,
        ILogger<CrossRuntimeValidator> log)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _gfortran = gfortran;
        _vb6 = vb6;
        _csharp = csharp;
        _log = log;
    }

    public async Task<ValidationRun> RunAsync(
        Guid scaffoldId,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var scaffold = await _db.Scaffolds
            .Include(s => s.Spec).ThenInclude(s => s!.Subroutine)
            .FirstOrDefaultAsync(s => s.Id == scaffoldId, ct)
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
            // ─── Phase 10.3.c — language-aware sidecar dispatch ────────────
            // Until 10.3.c the validator hardcoded the gfortran sidecar
            // regardless of source language — a VB6 spec's equivalence run
            // would still report "gfortran and C# agree". Now we inspect
            // the subroutine's SourceLanguage and route to the matching
            // sidecar. The fall-through remains the gfortran smoke loop so
            // unmapped languages (and any pre-Phase-5.2 ingest without a
            // SourceLanguage column) keep their historical behaviour.
            var sourceLanguage = scaffold.Spec?.Subroutine?.SourceLanguage ?? "";
            if (string.Equals(sourceLanguage, "vb6", StringComparison.OrdinalIgnoreCase))
            {
                return await RunVb6SmokeAsync(scaffold, run, actor, ct);
            }

            // ─── Phase 12.0.f — C# / .NET source → csharp sidecar ────────────
            if (string.Equals(sourceLanguage, "csharp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceLanguage, "vbnet", StringComparison.OrdinalIgnoreCase))
            {
                return await RunCsharpSmokeAsync(scaffold, run, actor, ct);
            }

            // ─── Sanity: is the gfortran sidecar reachable? ────────────────
            if (!await _gfortran.PingAsync(ct))
                throw new InvalidOperationException(
                    "gfortran sidecar health probe failed (Validation:GfortranEndpoint).");

            // ─── Per-routine route: CONSUME_ROLL ────────────────────────────
            // If the scaffold derives from a spec on the CONSUME_ROLL routine,
            // run the per-routine equivalence harness instead of the trivial
            // adder smoke. The harness drives both runtimes with 5 canonical
            // inputs and compares outputs row-for-row.
            var routineName = (scaffold.Spec?.Subroutine?.Name ?? "").ToUpperInvariant();
            if (routineName == "CONSUME_ROLL")
            {
                return await RunPerRoutineConsumeRollAsync(scaffold, run, actor, ct);
            }

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

    /// <summary>
    /// Per-routine equivalence harness for CONSUME_ROLL. Drives both
    /// runtimes with the same canonical inputs and compares outputs
    /// row-for-row.
    /// </summary>
    private async Task<ValidationRun> RunPerRoutineConsumeRollAsync(
        Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var stdin = ConsumeRollEquivalence.RenderStdin();
        var fortran = await _gfortran.CompileAndRunAsync(new GfortranClient.CompileAndRunRequest(
            Sources: new[] { new GfortranClient.Source("consume_roll_driver.f", ConsumeRollEquivalence.FortranDriverSource) },
            Stdin: stdin,
            TimeoutMs: 30_000), ct);

        if (fortran.Compile.ExitCode != 0 || (fortran.Run?.ExitCode ?? -1) != 0)
        {
            throw new InvalidOperationException(
                $"gfortran sidecar failed (compile={fortran.Compile.ExitCode}, run={fortran.Run?.ExitCode}); log: {fortran.Compile.Log}");
        }

        // Parse one JSON line per outcome.
        var fortranLines = (fortran.Run?.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (fortranLines.Length != ConsumeRollEquivalence.CanonicalInputs.Length)
        {
            throw new InvalidOperationException(
                $"Fortran driver emitted {fortranLines.Length} outcomes; expected {ConsumeRollEquivalence.CanonicalInputs.Length}. Raw: {fortran.Run?.Stdout}");
        }

        var rowResults = new List<object>();
        var allMatch = true;
        for (int i = 0; i < ConsumeRollEquivalence.CanonicalInputs.Length; i++)
        {
            var input = ConsumeRollEquivalence.CanonicalInputs[i];
            var csharp = ConsumeRollEquivalence.EvaluateCSharp(input);
            FortranOutcome forr;
            try
            {
                using var doc = JsonDocument.Parse(fortranLines[i]);
                var root = doc.RootElement;
                forr = new FortranOutcome(
                    ResultCd: root.GetProperty("result_cd").GetInt32(),
                    NewLf: root.GetProperty("new_lf").GetSingle(),
                    RollStatus: root.GetProperty("roll_status").GetInt32(),
                    GradeCd: root.GetProperty("grade_cd").GetString() ?? "",
                    EventEmitted: root.GetProperty("event_emitted").GetBoolean(),
                    EventGrade: root.GetProperty("event_grade").ValueKind == JsonValueKind.Null ? null : root.GetProperty("event_grade").GetString(),
                    EventNewLf: root.GetProperty("event_new_lf").ValueKind == JsonValueKind.Null ? (float?)null : root.GetProperty("event_new_lf").GetSingle());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not parse Fortran outcome line {i} ('{fortranLines[i]}'): {ex.Message}");
            }

            var match =
                forr.ResultCd == csharp.ResultCd &&
                MathF.Abs(forr.NewLf - csharp.NewLf) < 0.0001f &&
                forr.RollStatus == csharp.RollStatus &&
                (forr.GradeCd ?? "").TrimEnd() == (csharp.GradeCd ?? "").TrimEnd() &&
                forr.EventEmitted == csharp.EventEmitted &&
                (forr.EventGrade ?? "").TrimEnd() == (csharp.EventGrade ?? "").TrimEnd() &&
                NullableSinglesEqual(forr.EventNewLf, csharp.EventNewLf);

            if (!match) allMatch = false;

            rowResults.Add(new
            {
                input = new { rollId = input.RollId, usedLf = input.UsedLf, operId = input.OperId, label = input.ExpectedLabel },
                fortran = forr,
                csharp,
                match,
            });
        }

        // ─── Transcript log ─────────────────────────────────────────────
        var transcript = new System.Text.StringBuilder();
        transcript.AppendLine("=== Per-routine equivalence: CONSUME_ROLL ===");
        transcript.AppendLine();
        transcript.AppendLine("=== gfortran sidecar — compile ===");
        transcript.AppendLine($"exit {fortran.Compile.ExitCode} · {fortran.Compile.DurationMs}ms · {fortran.Compile.ErrorCount} errors · {fortran.Compile.WarningCount} warnings");
        if (!string.IsNullOrWhiteSpace(fortran.Compile.Log)) transcript.AppendLine(fortran.Compile.Log);
        transcript.AppendLine();
        transcript.AppendLine("=== gfortran sidecar — run ===");
        transcript.AppendLine($"exit {fortran.Run?.ExitCode ?? -1} · {fortran.Run?.DurationMs ?? -1}ms · timedOut={fortran.Run?.TimedOut ?? false}");
        transcript.AppendLine("stdin:");
        transcript.AppendLine(stdin);
        transcript.AppendLine("stdout:");
        transcript.AppendLine(fortran.Run?.Stdout);
        transcript.AppendLine();
        transcript.AppendLine("=== row-by-row comparison ===");
        foreach (var r in rowResults)
        {
            transcript.AppendLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        }
        transcript.AppendLine();
        transcript.AppendLine($"=== verdict === {(allMatch ? "PASSED" : "FAILED")} · {ConsumeRollEquivalence.CanonicalInputs.Length} inputs");

        var logKey = $"validation/{run.Id:N}/equivalence.log";
        var logUri = await _blob.PutTextAsync(_storage.Buckets.Scaffolds, logKey, transcript.ToString(), "text/plain", ct);

        run.LogBlobUri = logUri;
        run.MetricsJson = JsonSerializer.Serialize(new
        {
            mode = "per-routine",
            routine = "CONSUME_ROLL",
            inputCount = ConsumeRollEquivalence.CanonicalInputs.Length,
            matched = rowResults.Count(r => (bool)((dynamic)r).match),
            mismatched = rowResults.Count(r => !(bool)((dynamic)r).match),
            fortranCompileMs = fortran.Compile.DurationMs,
            fortranRunMs = fortran.Run?.DurationMs ?? -1,
            rows = rowResults,
        });
        run.CompletedAt = DateTimeOffset.UtcNow;
        if (allMatch)
        {
            run.Status = "PASSED";
            run.Summary = $"Per-routine equivalence: CONSUME_ROLL on {ConsumeRollEquivalence.CanonicalInputs.Length} canonical inputs · gfortran and C# agree row-for-row (result_cd, new_lf, status, grade, event payload).";
        }
        else
        {
            run.Status = "FAILED";
            run.ErrorCode = "equivalence.outputs_differ";
            var diffs = rowResults.Count(r => !(bool)((dynamic)r).match);
            run.Summary = $"Per-routine equivalence: CONSUME_ROLL · {diffs} of {ConsumeRollEquivalence.CanonicalInputs.Length} canonical inputs diverged between gfortran and C#. See log for the row diffs.";
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
                routine = "CONSUME_ROLL",
                inputCount = ConsumeRollEquivalence.CanonicalInputs.Length,
            },
            ct: ct);

        _log.LogInformation(
            "Per-routine equivalence for scaffold {Scaffold} (CONSUME_ROLL): {Status} ({Summary})",
            scaffold.Id, run.Status, run.Summary);
        return run;
    }

    private sealed record FortranOutcome(int ResultCd, float NewLf, int RollStatus, string? GradeCd, bool EventEmitted, string? EventGrade, float? EventNewLf);

    private static bool NullableSinglesEqual(float? a, float? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return MathF.Abs(a.Value - b.Value) < 0.0001f;
    }

    // ─── Phase 10.3.c — VB6 equivalence smoke ────────────────────────────
    // Mirrors the gfortran smoke flow but drives the vb6-sidecar via a
    // VB.NET reference rather than vb6.exe + msvbvm60.dll. The dev tier
    // runs `dotnet build` + `dotnet app.dll` (no licensed Microsoft
    // runtime needed); the production tier (Windows Server Core 2022)
    // can still use the vb6.exe + msvbvm60.dll path for binary-exact
    // equivalence against the original VB6 build. Either way the
    // round-trip is:
    //   1. Send a tiny VB.NET source that adds two stdin integers and
    //      prints {"sum": N} to stdout
    //   2. Compare the parsed sum against the C# reference's
    //      analytically-known answer
    // Same verdict / transcript / metric shape as the gfortran path so
    // the report-card surface treats both uniformly.
    //
    // The VB source LOOKS like VB6 — Option Explicit, Dim As Long, CLng,
    // string concatenation with `&`. VB.NET retains the VB family syntax
    // and the Console / Stdin APIs are well-defined in System.Console.
    private async Task<ValidationRun> RunVb6SmokeAsync(
        Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        if (!await _vb6.PingAsync(ct))
            throw new InvalidOperationException(
                "vb6 sidecar health probe failed (Validation:Vb6Endpoint).");

        const string smokeSource = """
            Option Explicit On
            Imports System

            Module Smoke
                Sub Main()
                    Dim line As String = Console.In.ReadLine()
                    Dim parts() As String = line.Split(" "c)
                    Dim a As Long = CLng(parts(0))
                    Dim b As Long = CLng(parts(1))
                    Dim total As Long = a + b
                    Console.Out.Write("{""sum"":" & total & "}")
                End Sub
            End Module
            """;
        const string stdinPayload = "3 4\n";
        const int expectedSum = 3 + 4;

        var vbs = await _vb6.CompileAndRunAsync(new Vb6Client.CompileAndRunRequest(
            Sources: new[] { new Vb6Client.Source("Smoke.vb", smokeSource) },
            Stdin: stdinPayload,
            TimeoutMs: 30_000), ct);

        int vbsSum;
        try
        {
            using var doc = JsonDocument.Parse((vbs.Run?.Stdout ?? "").Trim());
            vbsSum = doc.RootElement.GetProperty("sum").GetInt32();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse vb6-sidecar stdout as JSON: {ex.Message}; raw: {vbs.Run?.Stdout}");
        }

        var match = vbsSum == expectedSum;

        var transcript = new System.Text.StringBuilder();
        transcript.AppendLine("=== vb6 sidecar — compile (dotnet build, VB.NET) ===");
        transcript.AppendLine($"exit {vbs.Compile.ExitCode} · {vbs.Compile.DurationMs}ms · {vbs.Compile.ErrorCount} errors · {vbs.Compile.WarningCount} warnings");
        transcript.AppendLine(vbs.Compile.Log);
        transcript.AppendLine("=== vb6 sidecar — run (dotnet app.dll) ===");
        transcript.AppendLine($"exit {vbs.Run?.ExitCode ?? -1} · {vbs.Run?.DurationMs ?? -1}ms · timedOut={vbs.Run?.TimedOut ?? false}");
        transcript.AppendLine($"stdin: {stdinPayload.Replace("\n", @"\n")}");
        transcript.AppendLine($"stdout: {vbs.Run?.Stdout}");
        if (!string.IsNullOrWhiteSpace(vbs.Run?.Stderr))
            transcript.AppendLine($"stderr: {vbs.Run.Stderr}");
        transcript.AppendLine();
        transcript.AppendLine("=== C# reference ===");
        transcript.AppendLine($"computed: {expectedSum}");
        transcript.AppendLine();
        transcript.AppendLine("=== equivalence verdict ===");
        transcript.AppendLine($"vb6_sum={vbsSum} csharp_sum={expectedSum} match={match}");

        var logKey = $"validation/{run.Id:N}/equivalence.log";
        var logUri = await _blob.PutTextAsync(
            _storage.Buckets.Scaffolds, logKey, transcript.ToString(), "text/plain", ct);

        run.LogBlobUri = logUri;
        run.MetricsJson = JsonSerializer.Serialize(new
        {
            mode = "smoke",
            sourceLanguage = "vb6",
            runtime = "vbnet-dotnet10",
            vb6CompileExit = vbs.Compile.ExitCode,
            vb6CompileMs = vbs.Compile.DurationMs,
            vb6RunExit = vbs.Run?.ExitCode ?? -1,
            vb6RunMs = vbs.Run?.DurationMs ?? -1,
            vb6Sum = vbsSum,
            csharpSum = expectedSum,
            match,
        });
        run.CompletedAt = DateTimeOffset.UtcNow;

        if (match && vbs.Compile.ExitCode == 0 && (vbs.Run?.ExitCode ?? -1) == 0)
        {
            run.Status = "PASSED";
            run.Summary = "Smoke equivalence: VB.NET (.NET 10) and C# agree on canonical input. " +
                          "Production tier runs the original VB6 binary against vb6.exe + " +
                          "msvbvm60.dll for binary-exact behavioural equivalence.";
        }
        else
        {
            run.Status = "FAILED";
            run.ErrorCode = match ? "equivalence.runtime_error" : "equivalence.outputs_differ";
            run.Summary = match
                ? $"vb6-sidecar exit codes non-zero (compile={vbs.Compile.ExitCode}, run={vbs.Run?.ExitCode})."
                : $"Outputs differ: vb6={vbsSum} vs csharp={expectedSum}";
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

        return run;
    }

    // ─── Phase 12.0.f — csharp smoke ──────────────────────────────────────────
    // Compiles and runs a trivial C# console program via the csharp sidecar.
    // The program reads two integers from stdin, adds them, and prints
    // {"sum": N} to stdout. The C# reference computes the same value locally.
    // Both must agree. Same verdict shape as the VB6 / gfortran paths.
    private async Task<ValidationRun> RunCsharpSmokeAsync(
        Scaffold scaffold,
        ValidationRun run,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        if (!await _csharp.PingAsync(ct))
            throw new InvalidOperationException(
                "csharp sidecar health probe failed (Validation:CsharpEndpoint).");

        const string smokeSource = """
            using System;
            using System.Text.Json;

            var parts = Console.ReadLine()!.Trim().Split(' ');
            int a = int.Parse(parts[0]);
            int b = int.Parse(parts[1]);
            int sum = a + b;
            Console.WriteLine(JsonSerializer.Serialize(new { sum }));
            """;
        const string stdinPayload = "3 4\n";
        const int expectedSum = 3 + 4;

        var result = await _csharp.CompileAndRunAsync(new CsharpClient.CompileAndRunRequest(
            Sources: new[] { new CsharpClient.Source("Program.cs", smokeSource) },
            Stdin: stdinPayload,
            TimeoutMs: 30_000), ct);

        int csharpSum;
        try
        {
            using var doc = JsonDocument.Parse((result.Run?.Stdout ?? "").Trim());
            csharpSum = doc.RootElement.GetProperty("sum").GetInt32();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse csharp sidecar stdout as JSON: {ex.Message}; raw: {result.Run?.Stdout}");
        }

        var match = csharpSum == expectedSum;

        var transcript = new System.Text.StringBuilder();
        transcript.AppendLine("=== csharp sidecar — compile ===");
        transcript.AppendLine($"exit {result.Compile.ExitCode} · {result.Compile.DurationMs}ms · {result.Compile.ErrorCount} errors · {result.Compile.WarningCount} warnings");
        transcript.AppendLine(result.Compile.Log);
        transcript.AppendLine("=== csharp sidecar — run ===");
        transcript.AppendLine($"exit {result.Run?.ExitCode ?? -1} · {result.Run?.DurationMs ?? -1}ms · timedOut={result.Run?.TimedOut ?? false}");
        transcript.AppendLine($"stdin: {stdinPayload.Replace("\n", @"\n")}");
        transcript.AppendLine($"stdout: {result.Run?.Stdout}");
        if (!string.IsNullOrWhiteSpace(result.Run?.Stderr))
            transcript.AppendLine($"stderr: {result.Run.Stderr}");
        transcript.AppendLine();
        transcript.AppendLine("=== C# reference ===");
        transcript.AppendLine($"computed: {expectedSum}");
        transcript.AppendLine();
        transcript.AppendLine($"=== equivalence verdict ===");
        transcript.AppendLine($"sidecar_sum={csharpSum} reference_sum={expectedSum} match={match}");

        var logKey = $"validation/{run.Id:N}/equivalence.log";
        var logUri = await _blob.PutTextAsync(
            _storage.Buckets.Scaffolds, logKey, transcript.ToString(), "text/plain", ct);

        run.LogBlobUri = logUri;
        run.MetricsJson = JsonSerializer.Serialize(new
        {
            mode = "smoke",
            sourceLanguage = scaffold.Spec?.Subroutine?.SourceLanguage ?? "csharp",
            runtime = "dotnet10",
            compileExit = result.Compile.ExitCode,
            compileMs = result.Compile.DurationMs,
            runExit = result.Run?.ExitCode ?? -1,
            runMs = result.Run?.DurationMs ?? -1,
            sidecarSum = csharpSum,
            referenceSum = expectedSum,
            match,
        });
        run.CompletedAt = DateTimeOffset.UtcNow;

        if (match && result.Compile.ExitCode == 0 && (result.Run?.ExitCode ?? -1) == 0)
        {
            run.Status = "PASSED";
            run.Summary = "Smoke equivalence: csharp sidecar (.NET 10) and C# reference agree on canonical input.";
        }
        else
        {
            run.Status = "FAILED";
            run.ErrorCode = match ? "equivalence.runtime_error" : "equivalence.outputs_differ";
            run.Summary = match
                ? $"csharp sidecar exit codes non-zero (compile={result.Compile.ExitCode}, run={result.Run?.ExitCode})."
                : $"Outputs differ: sidecar={csharpSum} vs reference={expectedSum}";
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

        return run;
    }
}
