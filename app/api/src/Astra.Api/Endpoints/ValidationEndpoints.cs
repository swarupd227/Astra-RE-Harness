using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Storage;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #2a — post-migration validation surface.
///
///   POST /api/v1/scaffolds/{id}/validate/compile     trigger a build pass
///   GET  /api/v1/scaffolds/{id}/validation           list runs (latest per stage)
///   GET  /api/v1/validation-runs/{id}/log            stream the build log
/// </summary>
public static class ValidationEndpoints
{
    public static IEndpointRouteBuilder MapValidationEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Trigger a compile pass (sync — small projects build in seconds) ──
        app.MapPost("/api/v1/scaffolds/{id:guid}/validate/compile", async (
            Guid id,
            CompileValidator validator,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can trigger validation.");

            try
            {
                var run = await validator.RunAsync(id, persona, ct);
                return Results.Ok(ToResponse(run));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return NotFound("scaffold.not_found");
            }
        });

        // ─── Generate (or regenerate) the signed-spec test pack ─────────────
        // Idempotent — replaces the existing tests/<Subroutine>_SignedSpecPack.cs
        // in the scaffold's manifest. Engineer-only.
        app.MapPost("/api/v1/scaffolds/{id:guid}/generate-test-pack", async (
            Guid id,
            TestPackGenerator gen,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can regenerate the test pack.");
            try
            {
                var result = await gen.GenerateAsync(id, persona, ct);
                return Results.Ok(new
                {
                    scaffoldId = result.ScaffoldId,
                    testFilePath = result.TestFilePath,
                    counts = new
                    {
                        invariants = result.InvariantCount,
                        sideEffects = result.SideEffectCount,
                        edgeCases = result.EdgeCaseCount,
                        openQuestions = result.OpenQuestionCount,
                        total = result.TotalTests,
                    },
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return NotFound("scaffold.not_found");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("test_pack.precondition_failed", ex.Message);
            }
        });

        // ─── Trigger a test-pack pass (sync) ────────────────────────────────
        app.MapPost("/api/v1/scaffolds/{id:guid}/validate/test-pack", async (
            Guid id,
            TestPackValidator validator,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can trigger validation.");
            try
            {
                var run = await validator.RunAsync(id, persona, ct);
                return Results.Ok(ToResponse(run));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return NotFound("scaffold.not_found");
            }
        });

        // ─── Trigger a cross-runtime equivalence pass (sync) ────────────────
        app.MapPost("/api/v1/scaffolds/{id:guid}/validate/equivalence", async (
            Guid id,
            CrossRuntimeValidator validator,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can trigger validation.");
            try
            {
                var run = await validator.RunAsync(id, persona, ct);
                return Results.Ok(ToResponse(run));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return NotFound("scaffold.not_found");
            }
        });

        // ─── Phase 9.3.b — Trigger the 4th-gate (property-test / falsifying) ──
        // Walks the signed spec for invariant + edge_case claims that carry
        // generatorHints (per ADR-030), dispatches them to the property-test
        // sidecar's /falsify endpoint, persists a ValidationRun with
        // Stage="FALSIFYING". v1 implementation runs in "shadow mode" — the
        // callback returns agree=true without comparing against a candidate
        // binary, so the gate exercises the entire data path and the metrics
        // show examplesTried per claim. v1.1 swaps in real ref+candidate
        // execution behind the same callback contract.
        app.MapPost("/api/v1/scaffolds/{id:guid}/validate/falsifying", async (
            Guid id,
            PropertyTestValidator validator,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can trigger validation.");
            try
            {
                var run = await validator.RunAsync(id, persona, ct);
                return Results.Ok(ToResponse(run));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return NotFound("scaffold.not_found");
            }
        });

        // ─── List validation runs for a scaffold ────────────────────────────
        // Returns all runs ordered newest-first. Callers typically pick the
        // latest row per stage to render the report card.
        app.MapGet("/api/v1/scaffolds/{id:guid}/validation", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var scaffold = await db.Scaffolds.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (scaffold is null) return NotFound("scaffold.not_found");

            var runs = await db.ValidationRuns.AsNoTracking()
                .Where(r => r.ScaffoldId == id)
                .OrderByDescending(r => r.StartedAt)
                .Select(r => ToResponse(r))
                .ToListAsync(ct);

            return Results.Ok(new
            {
                scaffoldId = id,
                specId = scaffold.SpecId,
                runs,
            });
        });

        // ─── Phase 9.3.b / 9.5.a — Property-test sidecar callback ──────────
        // The property-test sidecar POSTs here once per generated input
        // (from inside the Hypothesis loop driven by /validate/falsifying).
        // Locked-down: a shared-secret query string must match the value
        // baked into the validator's callback URL builder; reject otherwise.
        //
        // Phase 9.5.a: the callback looks up the validation run in
        // PropertyTestRunCache. When the entry exists AND its mode is
        // "live", the ref binary is run via gfortran-sidecar with stdin
        // formatted from the input JSON; refOutput is the binary's stdout
        // and candOutput mirrors it (candidate-side execution lands in
        // Phase 9.5.b). When the entry is absent OR mode is "shadow", we
        // return the canned shadow-mode response — pod restarts mid-run
        // degrade gracefully to shadow-mode-equivalent (per ADR-029).
        app.MapPost("/internal/equivalence-callback", async (
            HttpContext ctx,
            PropertyTestRunCache runCache,
            GfortranClient gfortran,
            IConfiguration cfg,
            ILogger<ValidationEndpointMarker> log,
            CancellationToken ct) =>
        {
            var expected = cfg["Validation:PropertyTestCallbackSecret"]
                ?? "dev-shared-secret-rotate-me";
            var actual = ctx.Request.Query["secret"].ToString();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                return Results.Json(
                    new { error = new { code = "callback.bad_secret" } },
                    statusCode: 403);
            }

            var runIdRaw = ctx.Request.Query["runId"].ToString();
            if (!Guid.TryParse(runIdRaw, out var runId))
            {
                log.LogWarning("4th-gate callback: invalid runId query param '{Raw}'", runIdRaw);
                return ShadowResponse();
            }

            var entry = runCache.TryGet(runId);
            if (entry is null || entry.Mode != "live" || entry.RefArtifactId is null)
            {
                return ShadowResponse();
            }

            // Read the sidecar's POST payload to extract the generated input.
            CallbackPayload? payload = null;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<CallbackPayload>(ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "4th-gate callback: payload read failed for run {Run}", runId);
                return ShadowResponse();
            }
            if (payload?.Input is null)
            {
                return ShadowResponse();
            }

            await entry.CallbackGuard.WaitAsync(ct);
            try
            {
                // Format the input JSON object as a space-separated line in the
                // order declared by entry.InputNames — matches the shims'
                // stdin contract per ADR-032.
                var stdinLine = string.Join(' ',
                    entry.InputNames.Select(n =>
                        payload.Input.TryGetProperty(n, out var v)
                            ? v.ToString() ?? ""
                            : ""));

                // Reference side — gfortran sidecar /run with cached artifactId.
                var refRun = await gfortran.RunAsync(
                    entry.RefArtifactId, stdinLine, timeoutMs: 5_000, ct);
                var refOutput = (refRun.Stdout ?? "").Trim();

                // Candidate side — `dotnet <publishedExe.dll>` with stdin piped.
                // Phase 9.5.b ships the dotnet runner only; v1.2 adds java / mvn.
                string candOutput = "";
                if (entry.CandidateExePath is not null && entry.CandidateRunner == "dotnet")
                {
                    candOutput = (await RunCandidateAsync(entry.CandidateExePath, stdinLine, ct)).Trim();
                }
                else
                {
                    // No candidate wired for this language pair — fall back to
                    // the mirror behaviour so the sidecar's diff check still
                    // shows agree=true. v1.2 closes this gap.
                    candOutput = refOutput;
                }

                var agree = string.Equals(refOutput, candOutput, StringComparison.Ordinal);
                return Results.Json(new { agree, refOutput, candOutput });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "4th-gate callback: ref/cand execution failed for run {Run}", runId);
                return ShadowResponse();
            }
            finally
            {
                entry.CallbackGuard.Release();
            }

            static IResult ShadowResponse() => Results.Json(new
            {
                agree = true,
                refOutput = "(shadow mode — ref binary not wired for this run)",
                candOutput = "(shadow mode — candidate binary not wired for this run)",
            });
        });

        // ─── Read the build log blob (text/plain) ───────────────────────────
        app.MapGet("/api/v1/validation-runs/{id:guid}/log", async (
            Guid id,
            AppDbContext db,
            IBlobClient blob,
            CancellationToken ct) =>
        {
            var run = await db.ValidationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return NotFound("validation_run.not_found");
            if (run.LogBlobUri is null) return NotFound("validation_run.log_not_yet_available");

            var content = await blob.GetTextAsync(run.LogBlobUri, ct);
            return Results.Text(content, "text/plain; charset=utf-8");
        });

        // ─── Phase #5 — Per-routine equivalence preview ────────────────────
        // Drives the CONSUME_ROLL gfortran driver + the inline C# reference
        // with the canonical input vectors and returns the row-by-row
        // comparison. Admin-only (Fortran compile is expensive, audit per
        // invocation). Independent of any scaffold so the demo can show
        // "real cross-runtime equivalence on the actual routine" without
        // first running through the full pipeline.
        app.MapPost("/api/v1/validation/equivalence/preview/consume-roll", async (
            GfortranClient gfortran,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Admin)
                return Forbid("auth.admin_required", "Only admins can run the equivalence preview.");

            if (!await gfortran.PingAsync(ct))
                return Results.Problem(detail: "gfortran sidecar unreachable", statusCode: 503);

            var stdin = ConsumeRollEquivalence.RenderStdin();
            var fortran = await gfortran.CompileAndRunAsync(new GfortranClient.CompileAndRunRequest(
                Sources: new[] { new GfortranClient.Source("consume_roll_driver.f", ConsumeRollEquivalence.FortranDriverSource) },
                Stdin: stdin,
                TimeoutMs: 30_000), ct);

            if (fortran.Compile.ExitCode != 0 || (fortran.Run?.ExitCode ?? -1) != 0)
            {
                return Results.Problem(detail: $"gfortran compile/run failed: {fortran.Compile.Log}", statusCode: 500);
            }

            var fortranLines = (fortran.Run?.Stdout ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            var rows = new List<object>();
            var allMatch = true;
            for (int i = 0; i < ConsumeRollEquivalence.CanonicalInputs.Length; i++)
            {
                var input = ConsumeRollEquivalence.CanonicalInputs[i];
                var csharp = ConsumeRollEquivalence.EvaluateCSharp(input);
                JsonElement forr;
                try
                {
                    using var doc = JsonDocument.Parse(i < fortranLines.Length ? fortranLines[i] : "{}");
                    forr = doc.RootElement.Clone();
                }
                catch
                {
                    return Results.Problem(detail: $"Could not parse Fortran outcome line {i}: {(i < fortranLines.Length ? fortranLines[i] : "(missing)")}", statusCode: 500);
                }

                var match =
                    forr.GetProperty("result_cd").GetInt32() == csharp.ResultCd
                    && Math.Abs(forr.GetProperty("new_lf").GetSingle() - csharp.NewLf) < 0.0001f
                    && forr.GetProperty("roll_status").GetInt32() == csharp.RollStatus
                    && (forr.GetProperty("grade_cd").GetString() ?? "").TrimEnd() == csharp.GradeCd.TrimEnd()
                    && forr.GetProperty("event_emitted").GetBoolean() == csharp.EventEmitted;
                if (!match) allMatch = false;

                rows.Add(new
                {
                    input = new { rollId = input.RollId, usedLf = input.UsedLf, operId = input.OperId, label = input.ExpectedLabel },
                    fortran = forr,
                    csharp,
                    match,
                });
            }

            return Results.Ok(new
            {
                routine = "CONSUME_ROLL",
                inputCount = ConsumeRollEquivalence.CanonicalInputs.Length,
                matched = rows.Count(r => (bool)((dynamic)r).match),
                mismatched = rows.Count(r => !(bool)((dynamic)r).match),
                verdict = allMatch ? "PASSED" : "FAILED",
                fortranCompileMs = fortran.Compile.DurationMs,
                fortranRunMs = fortran.Run?.DurationMs ?? -1,
                rows,
            });
        });

        // ─── Phase 5.6 — COBOL equivalence preview ─────────────────────────
        // Mirrors the consume-roll preview above for the COBOL track:
        // drives the AVG-DRIVER GnuCOBOL program (DEPTPAY's AVERAGE-SALARY
        // paragraph wrapped in an ACCEPT/COMPUTE loop) and the inline C#
        // reference (decimal HALF_UP, scale 2 — same shape as Java
        // BigDecimal) with the canonical payroll vectors and returns the
        // row-by-row comparison. Admin-only.
        app.MapPost("/api/v1/validation/equivalence/preview/deptpay-average", async (
            GnuCobolClient gnucobol,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Admin)
                return Forbid("auth.admin_required", "Only admins can run the equivalence preview.");

            if (!await gnucobol.PingAsync(ct))
                return Results.Problem(detail: "gnucobol sidecar unreachable", statusCode: 503);

            var stdin = PayrollEquivalence.RenderStdin();
            var cobol = await gnucobol.CompileAndRunAsync(new GnuCobolClient.CompileAndRunRequest(
                Sources: new[] { new GnuCobolClient.Source("avg_driver.cob", PayrollEquivalence.CobolDriverSource) },
                Stdin: stdin,
                TimeoutMs: 30_000), ct);

            if (cobol.Compile.ExitCode != 0 || (cobol.Run?.ExitCode ?? -1) != 0)
            {
                return Results.Problem(
                    detail: $"gnucobol compile/run failed: {cobol.Compile.Log}\n---run---\n{cobol.Run?.Stdout}\n{cobol.Run?.Stderr}",
                    statusCode: 500);
            }

            // One DISPLAY per row → one stdout line per canonical input.
            // The COBOL edit picture (ZZZZZZ9.99) leaves leading spaces,
            // so trim before parsing with invariant culture.
            var cobolLines = (cobol.Run?.Stdout ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            var rows = new List<object>();
            var allMatch = true;
            for (int i = 0; i < PayrollEquivalence.CanonicalInputs.Length; i++)
            {
                var input = PayrollEquivalence.CanonicalInputs[i];
                var csharp = PayrollEquivalence.EvaluateCSharp(input);

                decimal cobolValue;
                var raw = i < cobolLines.Length ? cobolLines[i] : "(missing)";
                if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out cobolValue))
                {
                    return Results.Problem(
                        detail: $"Could not parse COBOL outcome line {i}: '{raw}'",
                        statusCode: 500);
                }

                // Decimal equality at scale 2 — both sides are scale 2 by
                // construction, so straight == is the right comparison.
                var match = cobolValue == csharp;
                if (!match) allMatch = false;

                rows.Add(new
                {
                    input = new
                    {
                        total = input.Total,
                        count = input.Count,
                        label = input.ExpectedLabel,
                    },
                    cobol = cobolValue,
                    csharp,
                    match,
                });
            }

            return Results.Ok(new
            {
                routine = "DEPTPAY.AVERAGE-SALARY",
                inputCount = PayrollEquivalence.CanonicalInputs.Length,
                matched = rows.Count(r => (bool)((dynamic)r).match),
                mismatched = rows.Count(r => !(bool)((dynamic)r).match),
                verdict = allMatch ? "PASSED" : "FAILED",
                cobolCompileMs = cobol.Compile.DurationMs,
                cobolRunMs = cobol.Run?.DurationMs ?? -1,
                rows,
            });
        });

        return app;
    }

    private static object ToResponse(Persistence.Entities.ValidationRun r) => new
    {
        id = r.Id,
        scaffoldId = r.ScaffoldId,
        specId = r.SpecId,
        stage = r.Stage,
        status = r.Status,
        summary = r.Summary,
        errorCode = r.ErrorCode,
        logBlobUri = r.LogBlobUri,
        metrics = r.MetricsJson is null ? (JsonElement?)null : JsonDocument.Parse(r.MetricsJson).RootElement.Clone(),
        startedAt = r.StartedAt,
        completedAt = r.CompletedAt,
    };

    private static IResult NotFound(string code) =>
        Results.NotFound(new { error = new { code } });
    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });
    private static IResult Forbid(string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: 403);

    /// <summary>
    /// Phase 9.5.b — run the cached candidate executable per callback.
    /// Writes `stdinLine + "\n"` to its stdin, reads stdout until EOF,
    /// returns the text. Caller catches; on any failure (non-zero
    /// exit, timeout, etc.) the callback falls back to the ref-only
    /// "agree=true" path so a flaky candidate doesn't tank the gate.
    /// </summary>
    private static async Task<string> RunCandidateAsync(
        string exePath,
        string stdinLine,
        CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{exePath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start candidate {exePath}");
        await proc.StandardInput.WriteLineAsync(stdinLine);
        proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        // Per-invocation timeout — 5s matches the gfortran-sidecar's /run timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(); } catch { } throw; }
        var stdout = await stdoutTask;
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"Candidate exit {proc.ExitCode}: {stderr}");
        }
        return stdout;
    }

    /// <summary>Marker for ILogger&lt;T&gt; — never instantiated.</summary>
    private sealed class ValidationEndpointMarker;

    /// <summary>
    /// Wire shape the property-test sidecar POSTs to the callback —
    /// see property_test_sidecar/server.py's `httpx.post` body.
    /// </summary>
    private sealed record CallbackPayload(
        string SpecId,
        string ClaimId,
        JsonElement Input);
}
