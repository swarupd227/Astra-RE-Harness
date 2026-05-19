using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 7.1 — Cross-routine harmonisation API surface.
///
///   POST   /api/v1/corpora/{id}/harmonise       admin-only, runs the pass
///   GET    /api/v1/corpora/{id}/harmonisation   list runs for a corpus
///   GET    /api/v1/harmonisation/runs/{id}      run detail + findings
///   PUT    /api/v1/harmonisation/findings/{id}  admin-only, update status
///                                               (open | accepted | dismissed)
///
/// All write paths are audit-logged. Read paths are open to any
/// authenticated persona (the findings are part of the audit trail).
/// </summary>
public static class HarmonisationEndpoints
{
    public static IEndpointRouteBuilder MapHarmonisationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/corpora/{id:guid}/harmonise", async (
            Guid id,
            HarmonisationPipeline pipeline,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            try
            {
                var run = await pipeline.RunAsync(id, actor, ct);
                return Results.Ok(RenderRunSummary(run));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = new { code = "harmonisation.precondition_failed", message = ex.Message } });
            }
        });

        app.MapGet("/api/v1/corpora/{id:guid}/harmonisation", async (
            Guid id, AppDbContext db, int? limit, CancellationToken ct) =>
        {
            var rows = await db.HarmonisationRuns.AsNoTracking()
                .Where(r => r.CorpusId == id)
                .OrderByDescending(r => r.CompletedAt)
                .Take(Math.Clamp(limit ?? 50, 1, 200))
                .ToListAsync(ct);
            return Results.Ok(new { data = rows.Select(RenderRunSummary) });
        });

        app.MapGet("/api/v1/harmonisation/runs/{id:guid}", async (
            Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.HarmonisationRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null)
                return Results.NotFound(new { error = new { code = "harmonisation_run.not_found" } });

            var findings = await db.HarmonisationFindings.AsNoTracking()
                .Where(f => f.HarmonisationRunId == id)
                .OrderBy(f => f.Severity == "high" ? 0 : f.Severity == "medium" ? 1 : 2)
                .ThenBy(f => f.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                run = RenderRunSummary(run),
                findings = findings.Select(RenderFinding),
            });
        });

        app.MapPut("/api/v1/harmonisation/findings/{id:guid}", async (
            Guid id,
            FindingUpdateRequest body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var finding = await db.HarmonisationFindings.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (finding is null)
                return Results.NotFound(new { error = new { code = "harmonisation_finding.not_found" } });

            var normalized = (body.Status ?? "").ToLowerInvariant();
            if (normalized is not ("open" or "accepted" or "dismissed"))
                return Results.BadRequest(new { error = new { code = "harmonisation.bad_status", message = "status must be one of open|accepted|dismissed." } });

            finding.Status = normalized;
            if (body.AdminNote is not null) finding.AdminNote = body.AdminNote;
            finding.UpdatedAt = DateTimeOffset.UtcNow;
            finding.UpdatedBy = actor.DisplayName;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "harmonisation.finding_updated", "harmonisation_finding", finding.Id, actor,
                payload: new { status = normalized, hasNote = body.AdminNote is not null },
                ct: ct);

            return Results.Ok(RenderFinding(finding));
        });

        return app;
    }

    private static object RenderRunSummary(HarmonisationRun r) => new
    {
        id = r.Id,
        corpusId = r.CorpusId,
        sourceVersionId = r.SourceVersionId,
        status = r.Status,
        promptId = r.PromptId,
        promptVersion = r.PromptVersion,
        modelName = r.ModelName,
        inputTokens = r.InputTokens,
        outputTokens = r.OutputTokens,
        cacheReadTokens = r.CacheReadTokens,
        cacheCreationTokens = r.CacheCreationTokens,
        specCount = r.SpecCount,
        findingCount = r.FindingCount,
        summary = r.Summary,
        errorMessage = r.ErrorMessage,
        triggeredBy = r.TriggeredBy,
        startedAt = r.StartedAt,
        completedAt = r.CompletedAt,
    };

    private static object RenderFinding(HarmonisationFinding f) => new
    {
        id = f.Id,
        harmonisationRunId = f.HarmonisationRunId,
        category = f.Category,
        severity = f.Severity,
        title = f.Title,
        detail = f.Detail,
        affectedSpecIds = ParseGuidArray(f.AffectedSpecIdsJson),
        status = f.Status,
        adminNote = f.AdminNote,
        createdAt = f.CreatedAt,
        updatedAt = f.UpdatedAt,
        updatedBy = f.UpdatedBy,
    };

    private static IReadOnlyList<string> ParseGuidArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }
        catch { return Array.Empty<string>(); }
    }

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}

public sealed record FindingUpdateRequest(string? Status, string? AdminNote);
