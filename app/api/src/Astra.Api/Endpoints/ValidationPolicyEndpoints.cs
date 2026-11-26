using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #7 — Validation policy surface.
///
///   GET /api/v1/validation/policy            policy (canonical merged with admin override)
///   PUT /api/v1/validation/policy            admin-only, replace the override (audit-logged)
///   DELETE /api/v1/validation/policy/override  admin-only, drop the override → revert to canonical
///
/// Persistence: the override lives in <c>platform_configs</c> keyed
/// "validation.policy"; absent row = canonical. The canonical default
/// is the static constant below.
/// </summary>
public static class ValidationPolicyEndpoints
{
    private const string ConfigKey = "validation.policy";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static IEndpointRouteBuilder MapValidationPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/validation/policy", async (AppDbContext db, CancellationToken ct) =>
        {
            var policy = await LoadEffectiveAsync(db, ct);
            return Results.Ok(policy);
        });

        app.MapPut("/api/v1/validation/policy", async (
            PolicyOverride body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            if (body.Gates is null || body.Gates.Count == 0)
                return Results.BadRequest(new { error = new { code = "policy.invalid", message = "At least one gate override required." } });

            // Validate each gate id against the canonical set
            var validGateIds = new HashSet<string>(CanonicalGates.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var g in body.Gates)
            {
                if (!validGateIds.Contains(g.Id))
                    return Results.BadRequest(new { error = new { code = "policy.unknown_gate", message = $"Unknown gate id '{g.Id}'. Known: {string.Join(", ", validGateIds)}." } });
            }
            if (body.RetryDefaults is { AutoRetryCount: < 0 or > 10 })
                return Results.BadRequest(new { error = new { code = "policy.invalid_retry", message = "autoRetryCount must be 0–10." } });

            // Upsert into platform_configs
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var existing = await db.PlatformConfigs.FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
            if (existing is null)
            {
                await db.PlatformConfigs.AddAsync(new PlatformConfig
                {
                    Key = ConfigKey,
                    ValueJson = json,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = null,
                    UpdatedByDisplay = actor.DisplayName,
                }, ct);
            }
            else
            {
                existing.ValueJson = json;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.UpdatedByDisplay = actor.DisplayName;
            }
            await db.SaveChangesAsync(ct);

            await audit.LogAsync("validation.policy_updated", "platform_config", null, actor,
                payload: new { key = ConfigKey, override_ = body }, ctx, ct);

            var merged = await LoadEffectiveAsync(db, ct);
            return Results.Ok(merged);
        });

        app.MapDelete("/api/v1/validation/policy/override", async (
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            var existing = await db.PlatformConfigs.FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
            if (existing is null) return Results.NoContent();
            db.PlatformConfigs.Remove(existing);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("validation.policy_reverted", "platform_config", null, actor,
                payload: new { key = ConfigKey }, ctx, ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>The admin's auto-retry count (0–10) when set above zero; otherwise null and the caller's default applies.</summary>
    public static async Task<int?> AutoRetryCountAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
        if (row is null) return null;
        try
        {
            var o = JsonSerializer.Deserialize<PolicyOverride>(row.ValueJson, JsonOpts);
            return o?.RetryDefaults?.AutoRetryCount is > 0 and var n ? n : null;
        }
        catch { return null; }
    }

    // ─── Effective-policy merge ──────────────────────────────────────────

    private static async Task<object> LoadEffectiveAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
        PolicyOverride? overrides = null;
        if (row is not null)
        {
            try { overrides = JsonSerializer.Deserialize<PolicyOverride>(row.ValueJson, JsonOpts); }
            catch { /* malformed override — fall back to canonical */ }
        }

        var gates = CanonicalGates.Select(g => MergeGate(g, overrides)).ToArray();
        var retry = overrides?.RetryDefaults ?? CanonicalRetryDefaults;
        var commit = overrides?.CommitGate ?? CanonicalCommitGate;

        return new
        {
            scope = "global",
            version = "v1.0",
            ownedBy = "Platform Admin",
            appliesTo = "Every scaffold artifact before commit.",
            commitGate = new
            {
                requireAllGreen = commit.RequireAllGreen,
                description = commit.Description ?? CanonicalCommitGate.Description,
            },
            gates,
            retryDefaults = new
            {
                transientFlakeWindow = retry.TransientFlakeWindow ?? CanonicalRetryDefaults.TransientFlakeWindow,
                autoRetryCount = retry.AutoRetryCount,
                note = retry.Note ?? CanonicalRetryDefaults.Note,
            },
            overrideActive = overrides is not null,
            overrideUpdatedAt = row?.UpdatedAt,
            overrideUpdatedBy = row?.UpdatedByDisplay,
        };
    }

    private static object MergeGate(GateCanonical canonical, PolicyOverride? overrides)
    {
        var o = overrides?.Gates?.FirstOrDefault(g => string.Equals(g.Id, canonical.Id, StringComparison.OrdinalIgnoreCase));
        return new
        {
            id = canonical.Id,
            label = canonical.Label,
            description = canonical.Description,
            required = o?.Required ?? canonical.Required,
            coverageThreshold = o?.CoverageThreshold ?? canonical.CoverageThreshold,
            retryPolicy = o?.RetryPolicy ?? canonical.RetryPolicy,
            blockingCommitOnFailure = o?.BlockingCommitOnFailure ?? canonical.BlockingCommitOnFailure,
        };
    }

    // ─── Canonical defaults ──────────────────────────────────────────────

    private sealed record GateCanonical(
        string Id, string Label, string Description,
        bool Required, string? CoverageThreshold, string RetryPolicy, string BlockingCommitOnFailure);

    private static readonly GateCanonical[] CanonicalGates =
    {
        new("compile", "Compile",
            "Build the generated package with its own toolchain (dotnet build, Maven, npm build, or a Python import of every module). The build must succeed with no errors; warnings are reported.",
            true, null, "manual", "yes"),
        new("test_pack", "Test pack",
            "Run every claim-mapped fixture in the generated test project. Every signed claim must produce at least one fixture and that fixture must pass.",
            true,
            "100% of signed claims must be covered by at least one fixture; coverage gap fails the gate.",
            "regenerate-then-run", "yes"),
        new("quality", "Quality (security · standards · maintainability)",
            "Read the generated package for security problems (secrets, injection, unsafe calls, disabled certificate checks, open CORS, exposed actuators), coding-standard breaches (Spring constructor injection and validated request bodies, logging instead of console output, no swallowed exceptions, typed TypeScript) and maintainability issues. Blocker and critical findings fail the gate; the rest are listed for review.",
            true,
            "Zero blocker or critical findings; major, minor and info findings are reported, not blocking.",
            "regenerate-then-run", "yes"),
        new("equivalence", "Cross-runtime equivalence",
            "Run the original program and the generated code on the same inputs and compare their outputs field by field. Runs for the source languages that have a reference runtime wired in; other languages rely on the test pack.",
            true,
            "At least one recorded input per routine where a reference runtime is available.",
            "manual", "yes"),
    };

    public sealed record CommitGateRecord(bool RequireAllGreen, string Description);
    private static readonly CommitGateRecord CanonicalCommitGate = new(
        true,
        "A scaffold cannot be committed to Git until every required gate reports PASSED on the same scaffold revision.");

    public sealed record RetryDefaultsRecord(string TransientFlakeWindow, int AutoRetryCount, string Note);
    private static readonly RetryDefaultsRecord CanonicalRetryDefaults = new(
        "PT15M",
        0,
        "Transient failures (a build server crash, a reference-runtime timeout) need a manual re-run by an engineer, so a flaky run never hides a real regression.");

    // ─── PUT request shape ───────────────────────────────────────────────

    public sealed class PolicyOverride
    {
        public List<GateOverride>? Gates { get; set; }
        public CommitGateRecord? CommitGate { get; set; }
        public RetryDefaultsRecord? RetryDefaults { get; set; }
    }
    public sealed class GateOverride
    {
        public string Id { get; set; } = "";
        public bool? Required { get; set; }
        public string? CoverageThreshold { get; set; }
        public string? RetryPolicy { get; set; }
        public string? BlockingCommitOnFailure { get; set; }
    }
}
