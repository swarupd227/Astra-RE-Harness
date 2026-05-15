using Astra.Api.Persistence;
using Astra.Api.Signing;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase C UX polish — endpoints powering the EvidenceTrail panel.
///
/// All three are read-only and intentionally small:
///
///   GET /api/v1/specs/{id}                       Spec-by-id with subroutine + corpus + signature + llmCall
///                                                 pre-joined for the trail panel.
///   GET /api/v1/specs/{id}/signed-manifest       The signed.json blob from MinIO (immutable archive).
///   GET /api/v1/signing-keys/{keyId}/public      The active key's PEM. Never returns the private key.
///
/// The manifest endpoint lets a verifier reproduce exactly what was signed
/// without trusting the live database (the canonical bytes are stored
/// verbatim in the blob).
/// </summary>
public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Spec by spec id (composite — for evidence/audit pages) ──
        app.MapGet("/api/v1/specs/{id:guid}", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var spec = await db.Specs
                .Include(s => s.LlmCall)
                .Include(s => s.Subroutine)
                    .ThenInclude(sub => sub!.SourceFile)
                        .ThenInclude(f => f!.SourceVersion)
                            .ThenInclude(v => v!.Corpus)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (spec is null) return Results.NotFound(new { error = new { code = "spec.not_found" } });

            var sig = await db.Signatures.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpecId == id, ct);

            var reviews = await db.ClaimReviews.AsNoTracking()
                .Where(r => r.SpecId == id)
                .ToListAsync(ct);
            var reviewBuckets = reviews
                .GroupBy(r => r.Action)
                .ToDictionary(g => g.Key, g => g.Count());

            var scaffold = await db.Scaffolds.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpecId == id, ct);

            return Results.Ok(new
            {
                id = spec.Id,
                state = spec.State,
                createdAt = spec.CreatedAt,
                updatedAt = spec.UpdatedAt,
                subroutine = spec.Subroutine is null ? null : new
                {
                    id = spec.Subroutine.Id,
                    name = spec.Subroutine.Name,
                    signature = spec.Subroutine.Signature,
                    lineStart = spec.Subroutine.LineStart,
                    lineEnd = spec.Subroutine.LineEnd,
                    state = spec.Subroutine.State,
                    commonBlockRefs = spec.Subroutine.CommonBlockRefs,
                    calledSubroutines = spec.Subroutine.CalledSubroutines,
                    file = spec.Subroutine.SourceFile is null ? null : new
                    {
                        id = spec.Subroutine.SourceFile.Id,
                        relativePath = spec.Subroutine.SourceFile.RelativePath,
                        lineCount = spec.Subroutine.SourceFile.LineCount,
                        fileHash = spec.Subroutine.SourceFile.FileHash,
                    },
                    corpus = spec.Subroutine.SourceFile?.SourceVersion?.Corpus is null ? null : new
                    {
                        id = spec.Subroutine.SourceFile.SourceVersion.Corpus.Id,
                        name = spec.Subroutine.SourceFile.SourceVersion.Corpus.Name,
                        sourceType = spec.Subroutine.SourceFile.SourceVersion.Corpus.SourceType,
                        sourceUrl = spec.Subroutine.SourceFile.SourceVersion.Corpus.SourceUrl,
                    },
                    sourceVersion = spec.Subroutine.SourceFile?.SourceVersion is null ? null : new
                    {
                        id = spec.Subroutine.SourceFile.SourceVersion.Id,
                        gitCommitHash = spec.Subroutine.SourceFile.SourceVersion.GitCommitHash,
                        ingestedAt = spec.Subroutine.SourceFile.SourceVersion.IngestedAt,
                    },
                },
                llmCall = spec.LlmCall is null ? null : new
                {
                    id = spec.LlmCall.Id,
                    provider = spec.LlmCall.Provider,
                    model = spec.LlmCall.Model,
                    promptTemplateId = spec.LlmCall.PromptTemplateId,
                    promptTemplateVersion = spec.LlmCall.PromptTemplateVersion,
                    providerConfigVersion = spec.LlmCall.ProviderConfigVersion,
                    inputTokens = spec.LlmCall.InputTokens,
                    outputTokens = spec.LlmCall.OutputTokens,
                    latencyMs = spec.LlmCall.LatencyMs,
                    costUsd = spec.LlmCall.CostUsd,
                    calledAt = spec.LlmCall.CalledAt,
                },
                review = new
                {
                    total = reviews.Count,
                    byAction = reviewBuckets,
                },
                signature = sig is null ? null : new
                {
                    id = sig.Id,
                    signedAt = sig.SignedAt,
                    signerDisplay = sig.SignerDisplay,
                    algorithm = sig.Algorithm,
                    keyId = sig.SignatureKeyId,
                    specCanonicalHash = sig.SpecCanonicalHash,
                    sourceVersionHash = sig.SourceVersionHash,
                    signatureBase64 = Convert.ToBase64String(sig.SignatureBytes),
                    signedBlobUri = sig.SignedBlobUri,
                },
                scaffold = scaffold is null ? null : new
                {
                    id = scaffold.Id,
                    state = scaffold.State,
                    targetPlatform = scaffold.TargetPlatform,
                    fileCount = scaffold.FileCount,
                    totalLines = scaffold.TotalLines,
                    todoCount = scaffold.TodoCount,
                    gitBranch = scaffold.GitBranch,
                    gitCommitHash = scaffold.GitCommitHash,
                    gitCommitUrl = scaffold.GitCommitUrl,
                    generatedAt = scaffold.GeneratedAt,
                },
            });
        });

        // ── Signed manifest: stream the immutable blob ───────────────
        app.MapGet("/api/v1/specs/{id:guid}/signed-manifest", async (
            Guid id,
            AppDbContext db,
            IBlobClient blob,
            CancellationToken ct) =>
        {
            var sig = await db.Signatures.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpecId == id, ct);
            if (sig is null) return Results.NotFound(new { error = new { code = "signature.not_found" } });

            string content;
            try
            {
                content = await blob.GetTextAsync(sig.SignedBlobUri, ct);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = new
                    {
                        code = "signed_manifest.unreachable",
                        message = $"Signed blob {sig.SignedBlobUri} could not be read: {ex.Message}",
                    },
                }, statusCode: 502);
            }

            // Forward as JSON so the browser can parse directly.
            return Results.Text(content, contentType: "application/json");
        });

        // ── Public key (PEM, never the private key) ──────────────────
        app.MapGet("/api/v1/signing-keys/{keyId}/public", async (
            string keyId,
            AppDbContext db,
            IHsmSigner signer,
            CancellationToken ct) =>
        {
            var key = await db.SigningKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.Id == keyId, ct);

            // Defensive fallback: if we somehow boot without the row but the
            // signer has the key cached, return its PEM. Useful right after a
            // schema reset before the first sign.
            if (key is null)
            {
                if (string.Equals(keyId, signer.ActiveKeyId, StringComparison.OrdinalIgnoreCase))
                {
                    var pem = await signer.GetPublicKeyPemAsync(ct);
                    return Results.Ok(new
                    {
                        keyId,
                        algorithm = signer.Algorithm,
                        publicKeyPem = pem,
                        source = "signer-cache",
                    });
                }
                return Results.NotFound(new { error = new { code = "key.not_found" } });
            }

            return Results.Ok(new
            {
                keyId = key.Id,
                algorithm = key.Algorithm,
                publicKeyPem = key.PublicKeyPem,
                createdAt = key.CreatedAt,
                source = "db",
            });
        });

        return app;
    }
}
