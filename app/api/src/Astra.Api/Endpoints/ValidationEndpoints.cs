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
}
