using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #8 — Signature Health surface.
///
///   GET /api/v1/specs/{id}/signature-health   per-spec drift verdict
///   GET /api/v1/signature-health              portfolio board (every signed spec)
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

        return app;
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
