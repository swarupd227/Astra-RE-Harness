using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Persistence;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class ScaffoldEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapScaffoldEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Stage-5 streaming generate ──────────────────────────────────
        app.MapPost("/api/v1/specs/{id:guid}/scaffold", async (
            Guid id,
            string? targetStack,
            HttpContext ctx,
            ScaffoldPipeline pipeline,
            ArchetypeRegistry archetypes,
            AppDbContext db,
            DevPersonaContext persona,
            ILogger<ScaffoldEndpointMarker> log,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = new { code = "auth.engineer_required", message = "Only engineers can generate scaffolds." }
                }, ct);
                return;
            }

            // Phase #4 / value-add #3 — target-stack selection.
            // Phase 5.4 — gate evaluates the archetype that actually
            // matches the spec's subroutine, not "first by stack". With
            // multiple archetypes per stack (e.g. java-spring has both
            // canonical-rollstock @ preview and cobol-canonical-payroll
            // @ production), the previous gate rejected production
            // requests whenever a preview archetype was registered first.

            // Look up the spec's subroutine name so PickForSubroutine
            // can match correctly. Spec → Subroutine join lives in the DB.
            var spec = await db.Specs
                .Include(s => s.Subroutine)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            var subroutineName = spec?.Subroutine?.Name ?? "";
            var sourceLanguage = spec?.Subroutine?.SourceLanguage ?? "";

            // Phase 10.3.b — when no targetStack is supplied, pick a sensible
            // default based on which target stacks actually have archetypes
            // compatible with this spec's schema. The previous "always
            // dotnet8" default silently scaffolded VB6 specs into the
            // alphabetical-first dotnet8 archetype (canonical-cpp-fmt) — a
            // foot-gun that took the whole demo recording session to
            // diagnose. Logic:
            //   - explicit ?targetStack=X wins, no matter what
            //   - else: stacks whose archetypes list `compatibleSchemas`
            //     including this spec's schemaId become the candidate set
            //   - prefer "dotnet8" if it's in the set (keeps Fortran /
            //     COBOL / Delphi / C++ on the well-trodden path)
            //   - otherwise pick the first stack alphabetically — VB6 has
            //     only "dotnet10" archetypes so this routes correctly
            //   - fallback to "dotnet8" if no archetype is compatible at
            //     all (preserves legacy behaviour)
            string chosenTarget;
            if (!string.IsNullOrWhiteSpace(targetStack))
            {
                chosenTarget = targetStack.Trim();
            }
            else
            {
                var compatibleStacks = archetypes.All()
                    .Where(a => string.IsNullOrEmpty(sourceLanguage)
                        || a.Manifest.CompatibleSchemas.Any(s =>
                            string.Equals(s, sourceLanguage, StringComparison.OrdinalIgnoreCase)))
                    .Select(a => a.Manifest.TargetStack)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                chosenTarget = compatibleStacks.Contains("dotnet8", StringComparer.OrdinalIgnoreCase)
                    ? "dotnet8"
                    : (compatibleStacks.FirstOrDefault() ?? "dotnet8");
                log.LogInformation(
                    "Scaffold default targetStack for spec {SpecId} (schema={Schema}): {Stack} (from candidates {Candidates})",
                    id, sourceLanguage, chosenTarget, string.Join(",", compatibleStacks));
            }

            // Any archetype registered for this target stack at all?
            var anyForTarget = archetypes.All()
                .Where(a => string.Equals(a.Manifest.TargetStack, chosenTarget, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (anyForTarget.Length == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "scaffold.unknown_target",
                        message = $"No archetype is registered for target stack '{chosenTarget}'. " +
                                  $"Available: {string.Join(", ", archetypes.All().Select(a => a.Manifest.TargetStack).Distinct())}.",
                    }
                }, ct);
                return;
            }

            // Phase 15.0.a — narrow to archetypes actually compatible with
            // this spec's source schema BEFORE falling back to "any
            // archetype for the stack". Without this, a schema with no
            // compatible archetype (or whose routine name doesn't match one)
            // would silently fall back to an unrelated language's archetype
            // (e.g. a java-sourced routine resolving to canonical-abl-*).
            var compatibleForTarget = anyForTarget
                .Where(a => a.Manifest.CompatibleSchemas.Count == 0
                    || string.IsNullOrEmpty(sourceLanguage)
                    || a.Manifest.CompatibleSchemas.Any(s => string.Equals(s, sourceLanguage, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (compatibleForTarget.Length == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "scaffold.no_compatible_archetype",
                        message = $"No archetype compatible with source language '{sourceLanguage}' is registered for target " +
                                  $"'{chosenTarget}'. This language/target pairing needs an archetype authored — propose one from " +
                                  "Pattern Analysis, or contact your Nous representative.",
                    }
                }, ct);
                return;
            }

            // Pick the archetype the scaffold pipeline will actually use.
            // Falls back to the first schema-compatible archetype for the
            // stack when the subroutine name doesn't match any anyOf clause —
            // same behaviour as ArchetypeRegistry.PickForSubroutine.
            var match = archetypes.PickForSubroutine(chosenTarget, subroutineName, sourceLanguage)
                ?? compatibleForTarget[0];

            // Production archetypes are self-service. Anything else is gated.
            var status = match.Manifest.Status ?? "";
            if (!status.StartsWith("production", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        code = "scaffold.target_gated",
                        message = $"Archetype '{match.Manifest.Id}' for target '{chosenTarget}' is currently {status}. " +
                                  "It ships as part of a Nous pair-engagement — contact your Nous representative to enable.",
                    }
                }, ct);
                return;
            }

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.Headers["Connection"] = "keep-alive";
            await ctx.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var evt in pipeline.RunAsync(id, chosenTarget, ct))
                {
                    await WriteEventAsync(ctx, evt, ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            catch (Exception ex)
            {
                log.LogError(ex, "Scaffold pipeline failed for spec {Id}", id);
                await WriteEventAsync(ctx, new ExtractionEvent("error", new
                {
                    code = "scaffold.unhandled_exception",
                    message = ex.Message,
                    retryable = true,
                }), ct);
            }
        });

        // ─── Read scaffold by spec id (the demo entry point) ─────────────
        app.MapGet("/api/v1/specs/{id:guid}/scaffold", async (
            Guid id,
            AppDbContext db,
            IBlobClient blob,
            CancellationToken ct) =>
        {
            var scaffold = await db.Scaffolds
                .Include(s => s.LlmCall)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpecId == id, ct);
            if (scaffold is null)
                return Results.NotFound(new { error = new { code = "scaffold.not_found" } });

            return Results.Ok(await ToResponseAsync(scaffold, blob, ct));
        });

        // ─── Read scaffold by id ─────────────────────────────────────────
        app.MapGet("/api/v1/scaffolds/{id:guid}", async (
            Guid id,
            AppDbContext db,
            IBlobClient blob,
            CancellationToken ct) =>
        {
            var scaffold = await db.Scaffolds
                .Include(s => s.LlmCall)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (scaffold is null)
                return Results.NotFound(new { error = new { code = "scaffold.not_found" } });

            return Results.Ok(await ToResponseAsync(scaffold, blob, ct));
        });

        // ─── Stub commit-to-Git ──────────────────────────────────────────
        // Phase B.4: records a faux commit hash + URL so the demo flow has a
        // realistic "committed" state. Real Octokit lands in Phase C.
        app.MapPost("/api/v1/scaffolds/{id:guid}/commit", async (
            Guid id,
            CommitRequest body,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            IConfiguration cfg,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can commit a scaffold.");

            var scaffold = await db.Scaffolds.Include(s => s.Spec).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (scaffold is null) return NotFound("scaffold.not_found");
            if (scaffold.State == "COMMITTED")
                return BadRequest("scaffold.already_committed", "This scaffold has already been committed.");

            // ── Validation gate (Phase #2a/2b) ──────────────────────────────
            // Commit is blocked until the latest COMPILE and TEST_PACK runs
            // are both PASSED. An explicit `force=true` flag in the request
            // body bypasses the gate but records that bypass in the audit log,
            // so a CIO can still trace down "who shipped without a green test pack."
            //
            // Gate is config-controlled (Validation:CommitGateRequired) so
            // existing e2e fixtures that pre-date the validation surface keep
            // passing. Production deployments flip it on.
            var gateRequired = cfg.GetValue("Validation:CommitGateRequired", false);
            if (gateRequired && body.Force != true)
            {
                var latestByStage = await db.ValidationRuns.AsNoTracking()
                    .Where(r => r.ScaffoldId == id)
                    .GroupBy(r => r.Stage)
                    .Select(g => g.OrderByDescending(r => r.StartedAt).First())
                    .ToListAsync(ct);

                var compile = latestByStage.FirstOrDefault(r => r.Stage == "COMPILE");
                var testPack = latestByStage.FirstOrDefault(r => r.Stage == "TEST_PACK");

                if (compile is null || compile.Status != "PASSED")
                    return BadRequest("commit.compile_not_passed",
                        compile is null
                            ? "Run POST /scaffolds/{id}/validate/compile before committing."
                            : $"Latest COMPILE run is {compile.Status} ({compile.Summary}). Re-validate before committing.");

                if (testPack is null || testPack.Status != "PASSED")
                    return BadRequest("commit.test_pack_not_passed",
                        testPack is null
                            ? "Run POST /scaffolds/{id}/validate/test-pack before committing."
                            : $"Latest TEST_PACK run is {testPack.Status} ({testPack.Summary}). Re-validate before committing.");

                var equivalence = latestByStage.FirstOrDefault(r => r.Stage == "EQUIVALENCE");
                if (equivalence is null || equivalence.Status != "PASSED")
                    return BadRequest("commit.equivalence_not_passed",
                        equivalence is null
                            ? "Run POST /scaffolds/{id}/validate/equivalence before committing."
                            : $"Latest EQUIVALENCE run is {equivalence.Status} ({equivalence.Summary}). Re-validate before committing.");
            }

            var branchName = string.IsNullOrWhiteSpace(body.Branch)
                ? $"scaffold/{(scaffold.Spec?.SubroutineId.ToString().Substring(0, 8))}-{DateTimeOffset.UtcNow:yyyyMMdd}"
                : body.Branch.Trim();

            // Faux commit hash + URL — Phase C swaps this for a real Octokit push.
            var fauxHash = Guid.NewGuid().ToString("N").Substring(0, 12);
            var fauxUrl = $"git://stub.local/astra-scaffold-output/commit/{fauxHash}";

            scaffold.State = "COMMITTED";
            scaffold.GitBranch = branchName;
            scaffold.GitCommitHash = fauxHash;
            scaffold.GitCommitUrl = fauxUrl;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "scaffold.committed",
                "spec", scaffold.SpecId,
                persona,
                new
                {
                    scaffoldId = scaffold.Id,
                    branch = branchName,
                    commitHash = fauxHash,
                    commitUrl = fauxUrl,
                    commitMessage = body.CommitMessage,
                    validationBypassed = body.Force == true,
                    note = "Phase B.4 stub commit — real Octokit push lands in Phase C.",
                },
                ctx, ct);

            return Results.Ok(new
            {
                id = scaffold.Id,
                state = scaffold.State,
                branch = branchName,
                commitHash = fauxHash,
                commitUrl = fauxUrl,
                stub = true,
            });
        });

        return app;
    }

    private static async Task<object> ToResponseAsync(
        Persistence.Entities.Scaffold scaffold, IBlobClient blob, CancellationToken ct)
    {
        var manifestText = await blob.GetTextAsync(scaffold.PackageBlobUri, ct);
        using var manifest = JsonDocument.Parse(manifestText);
        var files = manifest.RootElement.GetProperty("files").Clone();
        return new
        {
            id = scaffold.Id,
            specId = scaffold.SpecId,
            state = scaffold.State,
            targetPlatform = scaffold.TargetPlatform,
            fileCount = scaffold.FileCount,
            totalLines = scaffold.TotalLines,
            todoCount = scaffold.TodoCount,
            generatedAt = scaffold.GeneratedAt,
            packageBlobUri = scaffold.PackageBlobUri,
            git = scaffold.GitCommitHash is null ? null : new
            {
                branch = scaffold.GitBranch,
                commitHash = scaffold.GitCommitHash,
                commitUrl = scaffold.GitCommitUrl,
            },
            llmCall = scaffold.LlmCall is null ? null : new
            {
                provider = scaffold.LlmCall.Provider,
                model = scaffold.LlmCall.Model,
                promptTemplateId = scaffold.LlmCall.PromptTemplateId,
                promptTemplateVersion = scaffold.LlmCall.PromptTemplateVersion,
                providerConfigVersion = scaffold.LlmCall.ProviderConfigVersion,
                inputTokens = scaffold.LlmCall.InputTokens,
                outputTokens = scaffold.LlmCall.OutputTokens,
                latencyMs = scaffold.LlmCall.LatencyMs,
                costUsd = scaffold.LlmCall.CostUsd,
            },
            files,
        };
    }

    private static async Task WriteEventAsync(HttpContext ctx, ExtractionEvent evt, CancellationToken ct)
    {
        var dataJson = JsonSerializer.Serialize(evt.Data, JsonOpts);
        await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {dataJson}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static IResult NotFound(string code) => Results.NotFound(new { error = new { code } });
    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });
    private static IResult Forbid(string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: 403);

    private sealed class ScaffoldEndpointMarker;
}

public sealed record CommitRequest(string? Branch, string? CommitMessage, bool? Force = false);
