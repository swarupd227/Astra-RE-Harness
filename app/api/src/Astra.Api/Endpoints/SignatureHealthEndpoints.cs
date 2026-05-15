using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #8 — Signature Health surface.
///
///   GET  /api/v1/specs/{id}/signature-health   per-spec drift verdict
///   GET  /api/v1/signature-health              portfolio board (every signed spec)
///   POST /api/v1/specs/{id}/re-verify          admin-only: clear signature
///                                              + reset to IN_REVIEW so SME
///                                              can re-walk against new source
///   POST /api/v1/signature-health/re-verify-all admin-only: bulk re-verify
///                                              every drifted spec
///
/// "Healthy" means the corpus has not been re-ingested since this spec
/// was signed — the SourceVersion the signature is bound to is still
/// the latest version of the source. "Drift" means a newer ingest exists
/// and the signature should be re-verified against the new revision.
///
/// This is the productised version of the cryptographic-sign-off
/// promise: drift detection is automatic, not a hand-rolled audit.
/// </summary>
public static class SignatureHealthEndpoints
{
    public static IEndpointRouteBuilder MapSignatureHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/specs/{id:guid}/signature-health", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var verdict = await ComputeAsync(db, id, ct);
            if (verdict is null) return Results.NotFound(new { error = new { code = "spec.not_found" } });
            return Results.Ok(verdict);
        });

        app.MapGet("/api/v1/signature-health", async (
            AppDbContext db,
            CancellationToken ct) =>
        {
            // Every SIGNED (or post-SIGNED) spec — i.e. any spec with a
            // signature row. Drift verdict computed per spec.
            var specs = await db.Specs
                .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile).ThenInclude(f => f!.SourceVersion).ThenInclude(v => v!.Corpus)
                .Where(s => db.Signatures.Any(sig => sig.SpecId == s.Id))
                .AsNoTracking()
                .ToListAsync(ct);

            var rows = new List<object>(specs.Count);
            foreach (var spec in specs)
            {
                var v = await ComputeForSpecAsync(db, spec, ct);
                if (v is not null) rows.Add(v);
            }

            // Stable order: drift first, then by routine name.
            rows = rows
                .OrderByDescending(r => ((dynamic)r).state == "drift")
                .ThenBy(r => (string)((dynamic)r).routineName)
                .ToList();

            var driftCount = rows.Count(r => ((dynamic)r).state == "drift");
            return Results.Ok(new { totalSigned = rows.Count, drifted = driftCount, rows });
        });

        // ─── Phase #4.5 admin re-verify actions ──────────────────────────

        app.MapPost("/api/v1/specs/{id:guid}/re-verify", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);

            var (status, payload) = await ReverifyOneAsync(db, audit, actor, ctx, id, ct);
            return status switch
            {
                ReverifyStatus.NotFound       => Results.NotFound(new { error = new { code = "spec.not_found" } }),
                ReverifyStatus.NotSigned      => Results.BadRequest(new { error = new { code = "spec.not_signed", message = "Cannot re-verify an unsigned spec." } }),
                ReverifyStatus.NotDrifted     => Results.BadRequest(new { error = new { code = "spec.not_drifted", message = "Spec signature is healthy — no re-verify required." } }),
                _                             => Results.Ok(payload),
            };
        });

        app.MapPost("/api/v1/signature-health/re-verify-all", async (
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);

            // Find every spec whose signature is drifted (SourceVersionId !=
            // corpus's latest SourceVersion). Tracked load — we need to mutate.
            var specs = await db.Specs
                .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile).ThenInclude(f => f!.SourceVersion)
                .Where(s => db.Signatures.Any(sig => sig.SpecId == s.Id))
                .ToListAsync(ct);

            var reset = new List<Guid>();
            foreach (var spec in specs)
            {
                var (status, _) = await ReverifyOneAsync(db, audit, actor, ctx, spec.Id, ct);
                if (status == ReverifyStatus.Ok) reset.Add(spec.Id);
            }

            return Results.Ok(new { resetCount = reset.Count, specIds = reset });
        });

        return app;
    }

    private enum ReverifyStatus { Ok, NotFound, NotSigned, NotDrifted }

    private static async Task<(ReverifyStatus Status, object? Payload)> ReverifyOneAsync(
        AppDbContext db, IAuditLogger audit, DevPersonaContext actor, HttpContext ctx, Guid specId, CancellationToken ct)
    {
        var spec = await db.Specs
            .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile).ThenInclude(f => f!.SourceVersion)
            .FirstOrDefaultAsync(s => s.Id == specId, ct);
        if (spec is null) return (ReverifyStatus.NotFound, null);

        var signature = await db.Signatures.FirstOrDefaultAsync(s => s.SpecId == specId, ct);
        if (signature is null) return (ReverifyStatus.NotSigned, null);

        var corpusId = spec.Subroutine?.SourceFile?.SourceVersion?.CorpusId ?? Guid.Empty;
        var latestVersion = await db.SourceVersions
            .AsNoTracking()
            .Where(v => v.CorpusId == corpusId)
            .OrderByDescending(v => v.IngestedAt)
            .FirstOrDefaultAsync(ct);
        var healthy = latestVersion != null && latestVersion.Id == spec.SourceVersionId;
        if (healthy) return (ReverifyStatus.NotDrifted, null);

        // Clear the signature (it's already cryptographically invalid against
        // the new SourceVersion) and bounce the spec back to IN_REVIEW so the
        // SME can walk the deltas. Claim reviews are preserved as a starting
        // point — Phase D adds per-claim drift diffing.
        var prevSignatureId = signature.Id;
        var prevSourceVersionId = spec.SourceVersionId;
        db.Signatures.Remove(signature);

        // Re-point the spec to the latest source so the SME walks against
        // the current corpus on the same Spec id.
        if (latestVersion != null) spec.SourceVersionId = latestVersion.Id;
        spec.State = "IN_REVIEW";
        spec.UpdatedAt = DateTimeOffset.UtcNow;
        if (spec.Subroutine is not null)
        {
            spec.Subroutine.State = "IN_REVIEW";
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("spec.reverify_triggered", "spec", specId, actor, payload: new
        {
            previousSignatureId = prevSignatureId,
            previousSourceVersionId = prevSourceVersionId,
            newSourceVersionId = latestVersion?.Id,
            routineName = spec.Subroutine?.Name,
        }, ctx, ct);

        return (ReverifyStatus.Ok, new
        {
            specId,
            state = spec.State,
            previousSourceVersionId = prevSourceVersionId,
            newSourceVersionId = latestVersion?.Id,
            routineName = spec.Subroutine?.Name,
        });
    }

    private static async Task<object?> ComputeAsync(AppDbContext db, Guid specId, CancellationToken ct)
    {
        var spec = await db.Specs
            .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile).ThenInclude(f => f!.SourceVersion).ThenInclude(v => v!.Corpus)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == specId, ct);
        return spec is null ? null : await ComputeForSpecAsync(db, spec, ct);
    }

    private static async Task<object?> ComputeForSpecAsync(AppDbContext db, Astra.Api.Persistence.Entities.Spec spec, CancellationToken ct)
    {
        var signature = await db.Signatures.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SpecId == spec.Id, ct);
        var routineName = spec.Subroutine?.Name ?? "";
        var subroutineId = spec.Subroutine?.Id ?? Guid.Empty;
        var corpusId = spec.Subroutine?.SourceFile?.SourceVersion?.CorpusId ?? Guid.Empty;
        var corpusName = spec.Subroutine?.SourceFile?.SourceVersion?.Corpus?.Name ?? "";

        if (signature is null)
        {
            return new
            {
                specId = spec.Id,
                subroutineId,
                routineName,
                corpusId,
                corpusName,
                state = "unsigned",
                signedAt = (DateTimeOffset?)null,
            };
        }

        var latestVersion = await db.SourceVersions
            .AsNoTracking()
            .Where(v => v.CorpusId == corpusId)
            .OrderByDescending(v => v.IngestedAt)
            .FirstOrDefaultAsync(ct);

        var healthy = latestVersion != null && latestVersion.Id == spec.SourceVersionId;
        var state = healthy ? "healthy" : "drift";

        return new
        {
            specId = spec.Id,
            subroutineId,
            routineName,
            corpusId,
            corpusName,
            state,
            signedAt = (DateTimeOffset?)signature.SignedAt,
            signedSourceVersionId = spec.SourceVersionId,
            currentSourceVersionId = latestVersion?.Id,
            signedSourceHash = signature.SourceVersionHash,
            // Days since the NEWER ingest happened, or 0 if no drift.
            driftAgeDays = healthy || latestVersion == null
                ? 0
                : Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - latestVersion.IngestedAt).TotalDays)),
            signerDisplay = signature.SignerDisplay,
        };
    }
}
