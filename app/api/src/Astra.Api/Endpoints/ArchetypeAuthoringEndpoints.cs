using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm.PatternAnalysis;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 14.0 — live archetype authoring API surface.
///
///   POST /api/v1/pattern-clusters/{id}/propose-archetype   admin-only, LLM proposes + auto-verifies
///   GET  /api/v1/archetype-proposals/{id}                   proposal detail (files, compile log)
///   GET  /api/v1/corpora/{id}/archetype-proposals           list proposals for a corpus
///   POST /api/v1/archetype-proposals/{id}/approve           admin-only, requires VERIFIED, registers live
///   POST /api/v1/archetype-proposals/{id}/reject            admin-only
/// </summary>
public static class ArchetypeAuthoringEndpoints
{
    public static IEndpointRouteBuilder MapArchetypeAuthoringEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/pattern-clusters/{id:guid}/propose-archetype", async (
            Guid id,
            ArchetypeAuthoringService authoring,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            try
            {
                var proposal = await authoring.ProposeAsync(id, actor.DisplayName, ct);
                return Results.Ok(RenderProposal(proposal));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "archetype_proposal.precondition_failed", message = ex.Message },
                });
            }
        });

        app.MapGet("/api/v1/archetype-proposals/{id:guid}", async (
            Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var proposal = await db.ArchetypeProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (proposal is null)
                return Results.NotFound(new { error = new { code = "archetype_proposal.not_found" } });
            return Results.Ok(RenderProposalDetail(proposal));
        });

        app.MapGet("/api/v1/corpora/{id:guid}/archetype-proposals", async (
            Guid id, AppDbContext db, int? limit, CancellationToken ct) =>
        {
            var rows = await db.ArchetypeProposals.AsNoTracking()
                .Where(p => p.CorpusId == id)
                .OrderByDescending(p => p.CreatedAt)
                .Take(Math.Clamp(limit ?? 50, 1, 200))
                .ToListAsync(ct);
            return Results.Ok(new { data = rows.Select(RenderProposal) });
        });

        app.MapPost("/api/v1/archetype-proposals/{id:guid}/approve", async (
            Guid id,
            ArchetypeAuthoringService authoring,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            try
            {
                var proposal = await authoring.ApproveAsync(id, actor.DisplayName, ct);
                return Results.Ok(RenderProposal(proposal));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "archetype_proposal.approve_failed", message = ex.Message },
                });
            }
        });

        app.MapPost("/api/v1/archetype-proposals/{id:guid}/reject", async (
            Guid id,
            RejectProposalRequest body,
            ArchetypeAuthoringService authoring,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            if (string.IsNullOrWhiteSpace(body.Reason))
                return Results.BadRequest(new
                {
                    error = new { code = "archetype_proposal.reason_required", message = "A rejection reason is required." },
                });
            try
            {
                var proposal = await authoring.RejectAsync(id, body.Reason, ct);
                return Results.Ok(RenderProposal(proposal));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "archetype_proposal.reject_failed", message = ex.Message },
                });
            }
        });

        return app;
    }

    private static object RenderProposal(ArchetypeProposal p) => new
    {
        id = p.Id,
        patternClusterId = p.PatternClusterId,
        corpusId = p.CorpusId,
        targetStack = p.TargetStack,
        proposedArchetypeId = p.ProposedArchetypeId,
        displayName = p.DisplayName,
        description = p.Description,
        matches = ParseStringArray(p.MatchesJson),
        fileCount = CountFiles(p.FilesJson),
        state = p.State,
        compileErrorCount = p.CompileErrorCount,
        testCount = p.TestCount,
        testFailureCount = p.TestFailureCount,
        generatedBy = p.GeneratedBy,
        approvedBy = p.ApprovedBy,
        rejectedReason = p.RejectedReason,
        createdAt = p.CreatedAt,
        verifiedAt = p.VerifiedAt,
        decidedAt = p.DecidedAt,
    };

    private static object RenderProposalDetail(ArchetypeProposal p) => new
    {
        id = p.Id,
        patternClusterId = p.PatternClusterId,
        corpusId = p.CorpusId,
        targetStack = p.TargetStack,
        proposedArchetypeId = p.ProposedArchetypeId,
        displayName = p.DisplayName,
        description = p.Description,
        matches = ParseStringArray(p.MatchesJson),
        files = ParseFiles(p.FilesJson),
        state = p.State,
        compileLog = p.CompileLog,
        compileErrorCount = p.CompileErrorCount,
        testCount = p.TestCount,
        testFailureCount = p.TestFailureCount,
        generatedBy = p.GeneratedBy,
        approvedBy = p.ApprovedBy,
        rejectedReason = p.RejectedReason,
        createdAt = p.CreatedAt,
        verifiedAt = p.VerifiedAt,
        decidedAt = p.DecidedAt,
    };

    private static object ParseStringArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()) ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private static object ParseFiles(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()) ?? Array.Empty<object>();
        }
        catch { return Array.Empty<object>(); }
    }

    private static int CountFiles(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}

public sealed record RejectProposalRequest(string? Reason);
