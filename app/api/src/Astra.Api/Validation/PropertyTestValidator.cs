using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Phase 9.3.b — 4th-gate validation runner: walks the signed spec for
/// `invariant` + `edge_case` claims carrying `generatorHints` (per
/// ADR-030), dispatches them to the property-test sidecar's <c>/falsify</c>
/// endpoint, and persists a <see cref="ValidationRun"/> row with
/// <c>Stage = "FALSIFYING"</c>.
///
/// <para>
/// The sidecar drives the Hypothesis loop and posts each generated input
/// back to the API's <c>/internal/equivalence-callback</c> endpoint. In
/// the v1 implementation the callback runs the reference binary only and
/// returns <c>agree: true</c> with the ref output mirrored into the
/// candidate slot — i.e. "shadow mode". This exercises the entire data
/// path live; v1.1 wires real candidate execution behind the same
/// callback contract so the upgrade is purely additive.
/// </para>
///
/// <para>
/// A spec with zero hint-carrying claims is <b>PASSED with summary "no
/// hints"</b> — not FAILED — because "nothing to exercise" is honest
/// signal, not a regression. The validation card on the UI surfaces the
/// distinction.
/// </para>
/// </summary>
public sealed class PropertyTestValidator
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly PropertyTestClient _client;
    private readonly PropertyTestRunCache _runCache;
    private readonly GfortranClient _gfortran;
    private readonly IConfiguration _cfg;
    private readonly ILogger<PropertyTestValidator> _log;

    public PropertyTestValidator(
        AppDbContext db,
        IAuditLogger audit,
        PropertyTestClient client,
        PropertyTestRunCache runCache,
        GfortranClient gfortran,
        IConfiguration cfg,
        ILogger<PropertyTestValidator> log)
    {
        _db = db;
        _audit = audit;
        _client = client;
        _runCache = runCache;
        _gfortran = gfortran;
        _cfg = cfg;
        _log = log;
    }

    public async Task<ValidationRun> RunAsync(
        Guid scaffoldId,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var scaffold = await _db.Scaffolds.FirstOrDefaultAsync(s => s.Id == scaffoldId, ct)
            ?? throw new InvalidOperationException($"Scaffold {scaffoldId} not found.");

        var spec = await _db.Specs.FirstOrDefaultAsync(s => s.Id == scaffold.SpecId, ct)
            ?? throw new InvalidOperationException($"Spec {scaffold.SpecId} not found.");

        var run = new ValidationRun
        {
            Id = Guid.NewGuid(),
            ScaffoldId = scaffold.Id,
            SpecId = scaffold.SpecId,
            Stage = "FALSIFYING",
            Status = "RUNNING",
            Summary = "4th gate queued",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.ValidationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "validation.falsifying.started", "scaffold", scaffold.Id, actor,
            payload: new { runId = run.Id, stage = run.Stage, targetPlatform = scaffold.TargetPlatform },
            ct: ct);

        try
        {
            var claims = ExtractHintCarryingClaims(spec.SpecJson.RootElement);
            if (claims.Count == 0)
            {
                run.Status = "PASSED";
                run.Summary = "No claims carry generatorHints — 4th gate had nothing to exercise.";
                run.MetricsJson = JsonSerializer.Serialize(new
                {
                    mode = "shadow",
                    claimsExercised = 0,
                    falsifyingClaimIds = Array.Empty<string>(),
                    totalExamplesTried = 0,
                    perClaim = Array.Empty<object>(),
                    overallFalsified = false,
                });
                run.CompletedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);

                await _audit.LogAsync(
                    "validation.falsifying.passed", "scaffold", scaffold.Id, actor,
                    payload: new { runId = run.Id, stage = run.Stage, status = run.Status, reason = "no_hints" },
                    ct: ct);

                _log.LogInformation(
                    "4th-gate run {Run} on scaffold {Scaffold}: PASSED (no hints)",
                    run.Id, scaffold.Id);
                return run;
            }

            var callbackUrl = BuildCallbackUrl(run.Id);
            var language = ResolveLanguage(spec.SpecJson.RootElement);

            // Phase 9.5.a — decide whether this (schemaId, target) pair
            // qualifies for live mode. v1.1 enables live mode only for
            // fortran-f77 + dotnet8, gated behind a config flag so the
            // existing demo flow stays in shadow mode by default. v1.2
            // adds Delphi / C++ / COBOL paths behind the same predicate.
            var liveModeEnabled = _cfg.GetValue("Validation:LiveMode4thGate:Enabled", false);
            string mode = "shadow";
            if (liveModeEnabled && SupportsLiveMode(language, scaffold.TargetPlatform))
            {
                try
                {
                    var entry = await PrepareLiveModeAsync(claims, ct);
                    _runCache.Put(run.Id, entry);
                    mode = "live";
                    _log.LogInformation(
                        "4th-gate run {Run}: live mode armed — refSidecar={Sidecar} refArtifactId={Artifact}",
                        run.Id, entry.RefSidecar, entry.RefArtifactId);
                }
                catch (Exception liveEx)
                {
                    _log.LogWarning(liveEx,
                        "4th-gate run {Run}: live-mode preparation failed; falling back to shadow",
                        run.Id);
                    mode = "shadow";
                }
            }

            var request = new PropertyTestClient.FalsifyRequest(
                SpecId: spec.Id.ToString(),
                Language: language,
                CallbackUrl: callbackUrl,
                Claims: claims);

            PropertyTestClient.FalsifyResponse response;
            try
            {
                response = await _client.FalsifyAsync(request, ct);
            }
            finally
            {
                // Evict regardless of success / failure / exception — the
                // cache row is a "current run" indicator, not a result
                // cache. We also delete the candidate tempdir here so a
                // crashed validator leaves only one orphan tempdir per
                // crashed run (the startup sweep handles the rest).
                var entry = _runCache.TryGet(run.Id);
                _runCache.Remove(run.Id);
                if (entry?.CandidateTempDir is not null && Directory.Exists(entry.CandidateTempDir))
                {
                    try { Directory.Delete(entry.CandidateTempDir, recursive: true); }
                    catch (Exception cleanupEx) { _log.LogWarning(cleanupEx, "Candidate tempdir cleanup failed for run {Run}", run.Id); }
                }
            }

            var falsifyingIds = response.ClaimResults
                .Where(c => c.Falsifying.HasValue)
                .Select(c => c.ClaimId)
                .ToArray();

            run.MetricsJson = JsonSerializer.Serialize(new
            {
                mode,
                claimsExercised = response.ClaimResults.Count,
                falsifyingClaimIds = falsifyingIds,
                totalExamplesTried = response.ClaimResults.Sum(c => c.ExamplesTried),
                perClaim = response.ClaimResults.Select(c => new
                {
                    claimId = c.ClaimId,
                    examplesTried = c.ExamplesTried,
                    falsifying = c.Falsifying,
                    refOutput = c.RefOutput,
                    candOutput = c.CandOutput,
                    elapsedMs = c.ElapsedMs,
                    timedOut = c.TimedOut,
                    callbackErrors = c.CallbackErrors,
                    skipReason = c.SkipReason,
                }),
                overallFalsified = response.OverallFalsified,
                totalElapsedMs = response.TotalElapsedMs,
            });
            run.CompletedAt = DateTimeOffset.UtcNow;

            if (response.OverallFalsified)
            {
                run.Status = "FAILED";
                run.ErrorCode = "falsifying.counterexample_found";
                run.Summary = falsifyingIds.Length == 1
                    ? $"Falsifying example for {falsifyingIds[0]}"
                    : $"{falsifyingIds.Length} claims falsified";
            }
            else
            {
                run.Status = "PASSED";
                var timedOutClaims = response.ClaimResults.Count(c => c.TimedOut);
                run.Summary = timedOutClaims > 0
                    ? $"{response.ClaimResults.Count} claims exercised · {timedOutClaims} hit budget ({mode} mode)"
                    : $"{response.ClaimResults.Count} claims exercised · {response.ClaimResults.Sum(c => c.ExamplesTried)} examples ({mode} mode)";
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(
                response.OverallFalsified
                    ? "validation.falsifying.failed"
                    : "validation.falsifying.passed",
                "scaffold", scaffold.Id, actor,
                payload: new
                {
                    runId = run.Id,
                    stage = run.Stage,
                    status = run.Status,
                    summary = run.Summary,
                    falsifyingClaimIds = falsifyingIds,
                    totalElapsedMs = response.TotalElapsedMs,
                },
                ct: ct);

            _log.LogInformation(
                "4th-gate run {Run} on scaffold {Scaffold}: {Status} ({Summary})",
                run.Id, scaffold.Id, run.Status, run.Summary);

            return run;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Property-test validation crashed for scaffold {Scaffold}", scaffold.Id);
            run.Status = "ERRORED";
            run.ErrorCode = "falsifying.runner_crashed";
            run.Summary = $"Runner error: {ex.GetType().Name}: {ex.Message}";
            run.CompletedAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception saveEx) { _log.LogError(saveEx, "Could not persist ERRORED state"); }

            await _audit.LogAsync(
                "validation.falsifying.failed", "scaffold", scaffold.Id, actor,
                payload: new { runId = run.Id, stage = run.Stage, status = run.Status, error = ex.Message },
                ct: ct);
            return run;
        }
    }

    /// <summary>
    /// Walk the spec JSON for every invariant + edge_case claim whose
    /// `generatorHints` field is populated. Each hint must declare at
    /// least one input; claims with empty inputs are silently skipped
    /// (the sidecar would skip them anyway with `skipReason:
    /// no_inputs_in_generator_hints`).
    /// </summary>
    private static List<PropertyTestClient.ClaimSpec> ExtractHintCarryingClaims(JsonElement root)
    {
        var result = new List<PropertyTestClient.ClaimSpec>();
        foreach (var property in new[] { "invariants", "edge_cases" })
        {
            if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var claim in arr.EnumerateArray())
            {
                if (!claim.TryGetProperty("generatorHints", out var hints)
                    || hints.ValueKind != JsonValueKind.Object) continue;
                if (!hints.TryGetProperty("inputs", out var inputs)
                    || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() == 0) continue;
                if (!claim.TryGetProperty("id", out var idEl)
                    || idEl.ValueKind != JsonValueKind.String) continue;

                var parsedHints = ParseHints(hints);
                result.Add(new PropertyTestClient.ClaimSpec(
                    ClaimId: idEl.GetString()!,
                    GeneratorHints: parsedHints));
            }
        }
        return result;
    }

    private static PropertyTestClient.GeneratorHints ParseHints(JsonElement hints)
    {
        var inputs = new List<PropertyTestClient.InputHint>();
        foreach (var inp in hints.GetProperty("inputs").EnumerateArray())
        {
            inputs.Add(new PropertyTestClient.InputHint(
                Name: inp.GetProperty("name").GetString() ?? "",
                Type: inp.GetProperty("type").GetString() ?? "string",
                Min: inp.TryGetProperty("min", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetDouble() : (double?)null,
                Max: inp.TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetDouble() : (double?)null,
                MaxLen: inp.TryGetProperty("maxLen", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : (int?)null,
                Alphabet: inp.TryGetProperty("alphabet", out var alp) && alp.ValueKind == JsonValueKind.String ? alp.GetString() : null));
        }

        string? constraint = null;
        if (hints.TryGetProperty("constraint", out var c) && c.ValueKind == JsonValueKind.String)
            constraint = c.GetString();

        return new PropertyTestClient.GeneratorHints(inputs, constraint);
    }

    private string BuildCallbackUrl(Guid runId)
    {
        var baseUrl = _cfg["Validation:InternalCallbackBaseUrl"]
            ?? "http://api:8080";
        var secret = _cfg["Validation:PropertyTestCallbackSecret"]
            ?? "dev-shared-secret-rotate-me";
        return $"{baseUrl.TrimEnd('/')}/internal/equivalence-callback?runId={runId}&secret={Uri.EscapeDataString(secret)}";
    }

    // ──────────────────────────────────────────────────────────────────
    // Phase 9.5.a — live-mode opt-in + reference-binary pre-compile
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Does this (schemaId, target) pair support live-mode 4th-gate
    /// execution? v1.1 limits live mode to fortran-f77 + dotnet8 —
    /// the only pair with a HarnessDriver shim shipped in this phase.
    /// v1.2 expands to delphi / cpp / cobol behind the same predicate.
    /// </summary>
    private static bool SupportsLiveMode(string language, string targetPlatform)
        => string.Equals(language, "fortran-f77", StringComparison.OrdinalIgnoreCase)
        && string.Equals(targetPlatform, "dotnet8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compile both the reference binary (via gfortran-sidecar) and the
    /// candidate binary (via in-process `dotnet publish`) using the
    /// canonical echo HarnessDriver shims. Return a cache entry the
    /// callback uses for real ref-vs-cand comparison per generated
    /// input. The candidate's tempdir is owned by the entry and
    /// cleaned up on cache eviction.
    /// </summary>
    private async Task<PropertyTestRunCache.Entry> PrepareLiveModeAsync(
        IReadOnlyList<PropertyTestClient.ClaimSpec> claims,
        CancellationToken ct)
    {
        // 1. Reference binary — gfortran sidecar produces an artifactId.
        var compile = await _gfortran.CompileAndRunAsync(
            new GfortranClient.CompileAndRunRequest(
                Sources: new[]
                {
                    new GfortranClient.Source(
                        HarnessDriverShims.FortranSumShimPath,
                        HarnessDriverShims.FortranSumShimSource),
                },
                Stdin: "42\n",           // warm-up input to verify compile-and-run round-trip
                TimeoutMs: 30_000),
            ct);

        if (compile.Compile.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Ref-binary compile failed: {compile.Compile.Log}");
        }

        // 2. Candidate binary — `dotnet publish` of a tiny project in
        //    a per-run tempdir. The candidate-source-variant flag picks
        //    between the green-path echo and the intentional-break (the
        //    latter only used by the Δ-9.5 hard-gate criterion).
        var variant = _cfg.GetValue("Validation:LiveMode4thGate:CandidateVariant", "echo");
        var candidateSource = variant switch
        {
            "broken" => HarnessDriverShims.DotnetBrokenCandidateSource,
            _        => HarnessDriverShims.DotnetEchoCandidateSource,
        };
        var (candidateExe, candidateTempDir) =
            await PublishCandidateAsync(candidateSource, ct);

        // 3. Aggregate the input-field names; callback uses this to
        //    rename JSON keys into the shims' stdin line format.
        var inputNames = claims
            .SelectMany(c => c.GeneratorHints.Inputs.Select(i => i.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new PropertyTestRunCache.Entry(
            Mode: "live",
            RefSidecar: "gfortran",
            RefArtifactId: compile.Compile.ArtifactId,
            CandidateExePath: candidateExe,
            CandidateRunner: "dotnet",
            CandidateTempDir: candidateTempDir,
            InputNames: inputNames);
    }

    /// <summary>
    /// Write a tiny C# project to a per-run tempdir and run `dotnet
    /// publish` against it. Returns the absolute path to the produced
    /// assembly (`HarnessDriver.dll`) and the tempdir the entry should
    /// remember for eviction.
    /// </summary>
    private async Task<(string ExePath, string TempDir)> PublishCandidateAsync(
        string sourceText,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "astra-4th-gate-cand-" + Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(tempDir);

        var csprojPath = Path.Combine(tempDir, "HarnessDriver.csproj");
        var sourcePath = Path.Combine(tempDir, "Program.cs");
        var publishDir = Path.Combine(tempDir, "publish");
        await File.WriteAllTextAsync(csprojPath, HarnessDriverShims.DotnetCandidateProjectFile, ct);
        await File.WriteAllTextAsync(sourcePath, sourceText, ct);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{csprojPath}\" -c Release -o \"{publishDir}\" -v quiet --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = tempDir,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start `dotnet publish`.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            throw new InvalidOperationException(
                $"Candidate `dotnet publish` failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
        }

        var exePath = Path.Combine(publishDir, "HarnessDriver.dll");
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException(
                $"Candidate publish completed but {exePath} is missing.");
        }
        return (exePath, tempDir);
    }

    /// <summary>
    /// Resolve the property-test sidecar's `language` field from the spec
    /// metadata. The spec's `metadata.schema_id` or top-level `schema_id`
    /// is the authoritative source; falls back to "delphi" because the
    /// sidecar's only use for `language` today is logging.
    /// </summary>
    private static string ResolveLanguage(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var md)
            && md.ValueKind == JsonValueKind.Object
            && md.TryGetProperty("schema_id", out var mdSchema)
            && mdSchema.ValueKind == JsonValueKind.String)
            return mdSchema.GetString() ?? "unknown";
        if (root.TryGetProperty("schema_id", out var topSchema)
            && topSchema.ValueKind == JsonValueKind.String)
            return topSchema.GetString() ?? "unknown";
        return "unknown";
    }
}
