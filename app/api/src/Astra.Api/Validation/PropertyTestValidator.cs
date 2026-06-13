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
    private readonly IConfiguration _cfg;
    private readonly ILogger<PropertyTestValidator> _log;

    public PropertyTestValidator(
        AppDbContext db,
        IAuditLogger audit,
        PropertyTestClient client,
        IConfiguration cfg,
        ILogger<PropertyTestValidator> log)
    {
        _db = db;
        _audit = audit;
        _client = client;
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
            var request = new PropertyTestClient.FalsifyRequest(
                SpecId: spec.Id.ToString(),
                Language: language,
                CallbackUrl: callbackUrl,
                Claims: claims);

            var response = await _client.FalsifyAsync(request, ct);

            var falsifyingIds = response.ClaimResults
                .Where(c => c.Falsifying.HasValue)
                .Select(c => c.ClaimId)
                .ToArray();

            run.MetricsJson = JsonSerializer.Serialize(new
            {
                mode = "shadow",
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
                    ? $"{response.ClaimResults.Count} claims exercised · {timedOutClaims} hit budget (shadow mode)"
                    : $"{response.ClaimResults.Count} claims exercised · {response.ClaimResults.Sum(c => c.ExamplesTried)} examples (shadow mode)";
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
