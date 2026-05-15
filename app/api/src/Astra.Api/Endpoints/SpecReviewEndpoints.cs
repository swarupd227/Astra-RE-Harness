using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Signing;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class SpecReviewEndpoints
{
    public static IEndpointRouteBuilder MapSpecReviewEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Public verification key ─────────────────────────────────────
        app.MapGet("/api/v1/signing/jwks", async (IHsmSigner signer, CancellationToken ct) =>
        {
            var pem = await signer.GetPublicKeyPemAsync(ct);
            return Results.Ok(new
            {
                keyId = signer.ActiveKeyId,
                algorithm = signer.Algorithm,
                publicKeyPem = pem,
            });
        });

        // ─── Route a draft spec to review ────────────────────────────────
        app.MapPost("/api/v1/specs/{id:guid}/route", async (
            Guid id,
            RouteRequest body,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can route a spec to review.");

            var spec = await db.Specs.Include(s => s.Subroutine).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (spec is null) return NotFound("spec.not_found");

            if (spec.State != "DRAFT")
                return BadRequest("spec.invalid_state", $"Cannot route from state {spec.State}.");

            spec.State = "IN_REVIEW";
            spec.UpdatedAt = DateTimeOffset.UtcNow;
            if (spec.Subroutine is not null) spec.Subroutine.State = "IN_REVIEW";
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "spec.routed",
                "spec", spec.Id,
                persona,
                new
                {
                    subroutineId = spec.SubroutineId,
                    routingNote = body.RoutingNote,
                    reviewerIds = body.ReviewerIds,
                    fromState = "DRAFT",
                    toState = "IN_REVIEW",
                },
                ctx, ct);

            return Results.Ok(new { id = spec.Id, state = spec.State, routingNote = body.RoutingNote });
        });

        // ─── Claim-level SME action ──────────────────────────────────────
        // path is URL-encoded by the client; we receive it raw.
        app.MapPost("/api/v1/specs/{id:guid}/claims/review", async (
            Guid id,
            ClaimReviewRequest body,
            AppDbContext db,
            DevPersonaContext persona,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var spec = await db.Specs.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (spec is null) return NotFound("spec.not_found");
            if (spec.State != "IN_REVIEW")
                return BadRequest("spec.invalid_state", $"Claim review requires IN_REVIEW state (was {spec.State}).");

            // Validation
            if (string.IsNullOrWhiteSpace(body.ClaimPath))
                return BadRequest("claim.path_required", "claimPath is required.");
            if (body.Action is not ("accept" or "edit" or "reject" or "question"))
                return BadRequest("claim.invalid_action", "action must be accept | edit | reject | question.");
            if (body.Action == "edit" && string.IsNullOrWhiteSpace(body.EditedText))
                return BadRequest("claim.edited_text_required", "edited_text required for edit action.");
            if (body.Action == "reject" && (body.Reason is null || body.Reason.Length < 20))
                return BadRequest("claim.reason_too_short", "Reject reason must be ≥ 20 characters.");
            if (body.Action == "question" && string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest("claim.question_body_required", "question reason (body) is required.");

            // Upsert
            var existing = await db.ClaimReviews
                .FirstOrDefaultAsync(r => r.SpecId == id && r.ClaimPath == body.ClaimPath, ct);
            var now = DateTimeOffset.UtcNow;

            ClaimReview entity;
            string? previousAction = existing?.Action;
            if (existing is null)
            {
                entity = new ClaimReview
                {
                    Id = Guid.NewGuid(),
                    SpecId = id,
                    ClaimPath = body.ClaimPath,
                    Action = body.Action,
                    Reason = body.Reason,
                    EditedText = body.EditedText,
                    ReviewedAt = now,
                };
                await db.ClaimReviews.AddAsync(entity, ct);
            }
            else
            {
                existing.Action = body.Action;
                existing.Reason = body.Reason;
                existing.EditedText = body.EditedText;
                existing.ReviewedAt = now;
                entity = existing;
            }
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                $"claim.{body.Action}",
                "spec", id,
                persona,
                new
                {
                    claimReviewId = entity.Id,
                    claimPath = entity.ClaimPath,
                    action = entity.Action,
                    previousAction,
                    reason = entity.Reason,
                    editedText = entity.EditedText,
                },
                ctx, ct);

            return Results.Ok(ToDto(entity));
        });

        // ─── Sign the spec ───────────────────────────────────────────────
        app.MapPost("/api/v1/specs/{id:guid}/sign", async (
            Guid id,
            SignRequest body,
            AppDbContext db,
            DevPersonaContext persona,
            IHsmSigner signer,
            IBlobClient blob,
            StorageOptions storage,
            IAuditLogger audit,
            HttpContext ctx,
            ILogger<SignEndpointMarker> log,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Sme)
                return Forbid("auth.sme_required", "Only SMEs can sign a spec.");

            var spec = await db.Specs
                .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile)
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (spec is null) return NotFound("spec.not_found");

            // ── Idempotency fast-path ────────────────────────────────────
            // If a Signature already exists for this spec, return 200 with
            // the existing signature instead of failing. Also self-heal a
            // stuck IN_REVIEW state when the row exists (the Phase B.4 race:
            // signature inserted, MinIO write succeeded, but a subsequent
            // SaveChanges raced and didn't update Spec.state). The frontend
            // can treat the response as a no-op success and refresh.
            var existingSig = await db.Signatures.FirstOrDefaultAsync(s => s.SpecId == id, ct);
            if (existingSig is not null)
            {
                if (spec.State != "SIGNED")
                {
                    log.LogWarning(
                        "Self-healing stuck spec {SpecId}: signature exists but state was {State}",
                        spec.Id, spec.State);
                    spec.State = "SIGNED";
                    spec.UpdatedAt = DateTimeOffset.UtcNow;
                    if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";
                    await db.SaveChangesAsync(ct);
                }
                return Results.Ok(SignatureToDto(existingSig, spec.State, idempotent: true));
            }

            if (spec.State != "IN_REVIEW")
                return BadRequest("spec.invalid_state", $"Sign requires IN_REVIEW state (was {spec.State}).");

            // Precondition: every Invariant / Side effect / Edge case has a final review,
            // and every Open question has a non-question review.
            var preconditionFailures = await CheckPreconditionsAsync(db, spec, ct);
            if (preconditionFailures.Count > 0)
                return Results.BadRequest(new
                {
                    error = new
                    {
                        code = "spec.sign.preconditions_unmet",
                        message = "Sign-off requires every claim to be processed and every open question resolved.",
                        details = preconditionFailures,
                    },
                });

            if (string.IsNullOrWhiteSpace(body.Confirmation) ||
                !body.Confirmation.Contains("I have reviewed every claim", StringComparison.OrdinalIgnoreCase))
                return BadRequest("spec.sign.confirmation_required",
                    "The canonical confirmation sentence is required.");

            // Build the signed payload by applying claim reviews to the spec_json,
            // then canonicalise + hash + sign.
            var reviews = await db.ClaimReviews.Where(r => r.SpecId == id).ToListAsync(ct);
            var withReviews = ApplyReviews(spec.SpecJson, reviews);
            var canonical = CanonicalJson.Serialize(withReviews.RootElement);
            var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
            var canonicalHash = "sha256:" + Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
            var sourceVersionHash = "sha256:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(spec.SourceVersionId.ToString()))).ToLowerInvariant();

            var signatureBytes = await signer.SignAsync(canonicalBytes, ct);

            // Archive the signed payload to the immutable bucket. We store the
            // canonical bytes verbatim (`specCanonical`) so any verifier can
            // reproduce the input to the signature without having to
            // re-implement RFC-8785 canonicalisation.
            var signedManifest = new Dictionary<string, object?>
            {
                ["manifestVersion"] = 1,
                ["specId"] = spec.Id,
                ["subroutineId"] = spec.SubroutineId,
                ["sourceVersionId"] = spec.SourceVersionId,
                ["sourceVersionHash"] = sourceVersionHash,
                ["specCanonicalHash"] = canonicalHash,
                ["algorithm"] = signer.Algorithm,
                ["keyId"] = signer.ActiveKeyId,
                ["signatureBase64"] = Convert.ToBase64String(signatureBytes),
                ["signedAt"] = DateTimeOffset.UtcNow,
                ["signerDisplay"] = persona.DisplayName,
                ["specCanonical"] = canonical,
                ["spec"] = JsonDocument.Parse(canonical).RootElement,
            };
            var manifestJson = JsonSerializer.Serialize(signedManifest);
            var blobUri = await blob.PutTextAsync(
                storage.Buckets.SignedSpecs,
                $"{spec.Id}/{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json",
                manifestJson,
                "application/json",
                ct);

            // ── Persist Signature row + transition state in one transaction ──
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Re-check inside the transaction to absorb concurrent inserts that
            // raced past the fast-path above.
            var raceWinner = await db.Signatures.FirstOrDefaultAsync(s => s.SpecId == id, ct);
            if (raceWinner is not null)
            {
                if (spec.State != "SIGNED")
                {
                    spec.State = "SIGNED";
                    spec.UpdatedAt = DateTimeOffset.UtcNow;
                    if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";
                    await db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct);
                return Results.Ok(SignatureToDto(raceWinner, spec.State, idempotent: true));
            }

            var sig = new Signature
            {
                Id = Guid.NewGuid(),
                SpecId = spec.Id,
                SignerDisplay = persona.DisplayName,
                SignedAt = DateTimeOffset.UtcNow,
                SourceVersionHash = sourceVersionHash,
                SpecCanonicalHash = canonicalHash,
                SignatureBytes = signatureBytes,
                SignatureKeyId = signer.ActiveKeyId,
                Algorithm = signer.Algorithm,
                SignedBlobUri = blobUri,
            };
            await db.Signatures.AddAsync(sig, ct);

            spec.State = "SIGNED";
            spec.UpdatedAt = DateTimeOffset.UtcNow;
            spec.SpecJson = withReviews;
            if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Concurrent insert may have raced ours. If a signature exists
                // for this spec, treat as success and present the winner.
                // Otherwise rethrow so the caller sees the original failure.
                await tx.RollbackAsync(ct);
                var winner = await db.Signatures
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.SpecId == id, ct);
                if (winner is null) throw;
                // Re-apply the state transition outside the original transaction.
                var freshSpec = await db.Specs
                    .Include(s => s.Subroutine)
                    .FirstAsync(s => s.Id == id, ct);
                if (freshSpec.State != "SIGNED")
                {
                    freshSpec.State = "SIGNED";
                    freshSpec.UpdatedAt = DateTimeOffset.UtcNow;
                    if (freshSpec.Subroutine is not null) freshSpec.Subroutine.State = "SIGNED";
                    await db.SaveChangesAsync(ct);
                }
                return Results.Ok(SignatureToDto(winner, "SIGNED", idempotent: true));
            }

            log.LogInformation(
                "Signed spec {SpecId} key={KeyId} hash={Hash} blob={BlobUri}",
                spec.Id, sig.SignatureKeyId, sig.SpecCanonicalHash, sig.SignedBlobUri);

            await audit.LogAsync(
                "spec.signed",
                "spec", spec.Id,
                persona,
                new
                {
                    signatureId = sig.Id,
                    algorithm = sig.Algorithm,
                    keyId = sig.SignatureKeyId,
                    specCanonicalHash = sig.SpecCanonicalHash,
                    sourceVersionHash = sig.SourceVersionHash,
                    signedBlobUri = sig.SignedBlobUri,
                    fromState = "IN_REVIEW",
                    toState = "SIGNED",
                },
                ctx, ct);

            return Results.Ok(SignatureToDto(sig, spec.State, idempotent: false));
        });

        return app;
    }

    private static object SignatureToDto(Signature sig, string specState, bool idempotent) => new
    {
        id = sig.Id,
        specId = sig.SpecId,
        state = specState,
        signedAt = sig.SignedAt,
        signerDisplay = sig.SignerDisplay,
        algorithm = sig.Algorithm,
        keyId = sig.SignatureKeyId,
        specCanonicalHash = sig.SpecCanonicalHash,
        sourceVersionHash = sig.SourceVersionHash,
        signedBlobUri = sig.SignedBlobUri,
        signatureBase64 = Convert.ToBase64String(sig.SignatureBytes),
        idempotent,
    };

    // ─── Helpers ────────────────────────────────────────────────────────

    private static IResult NotFound(string code) =>
        Results.NotFound(new { error = new { code } });
    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });
    private static IResult Forbid(string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: 403);

    private static object ToDto(ClaimReview r) => new
    {
        id = r.Id,
        specId = r.SpecId,
        claimPath = r.ClaimPath,
        action = r.Action,
        reason = r.Reason,
        editedText = r.EditedText,
        reviewedAt = r.ReviewedAt,
    };

    private static async Task<List<object>> CheckPreconditionsAsync(
        AppDbContext db, Spec spec, CancellationToken ct)
    {
        var failures = new List<object>();
        var reviews = await db.ClaimReviews
            .Where(r => r.SpecId == spec.Id)
            .ToDictionaryAsync(r => r.ClaimPath, ct);

        var doc = spec.SpecJson.RootElement;

        void check(string section, string idField)
        {
            if (!doc.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                var el = arr[i];
                var idValue = el.TryGetProperty(idField, out var idEl) ? idEl.GetString() ?? $"{i}" : $"{i}";
                var path = ClaimPath.For(section, idValue);
                if (!reviews.TryGetValue(path, out var r))
                {
                    failures.Add(new { claimPath = path, issue = "untouched" });
                }
                else if (section == "open_questions" && r.Action == "question")
                {
                    failures.Add(new { claimPath = path, issue = "unresolved" });
                }
            }
        }

        check("invariants", "id");
        check("side_effects", "id");      // synthesised below if absent
        check("edge_cases", "id");
        check("open_questions", "id");
        return failures;
    }

    private static JsonDocument ApplyReviews(JsonDocument original, List<ClaimReview> reviews)
    {
        // For each reviewed claim, apply its action. Edits replace the claim text
        // (or description); rejections mark the claim with state="rejected"; accepts
        // simply tag state="accepted". Everything is preserved so the audit trail
        // can show the original LLM draft alongside SME edits.
        var node = JsonDocument.Parse(original.RootElement.GetRawText()).RootElement.Clone();
        var dict = JsonElementToDictionary(node);

        foreach (var review in reviews)
        {
            ApplyReviewMutate(dict, review);
        }

        var serialized = JsonSerializer.Serialize(dict);
        return JsonDocument.Parse(serialized);
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => JsonElementToDictionary(el),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static void ApplyReviewMutate(Dictionary<string, object?> spec, ClaimReview review)
    {
        var (section, id) = ClaimPath.Parse(review.ClaimPath);
        if (section is null || id is null) return;
        if (!spec.TryGetValue(section, out var arr) || arr is not List<object?> items) return;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not Dictionary<string, object?> obj) continue;
            obj.TryGetValue("id", out var idVal);
            if (idVal?.ToString() != id) continue;

            obj["sme_action"] = review.Action;
            obj["sme_reviewed_at"] = review.ReviewedAt;
            if (!string.IsNullOrEmpty(review.Reason)) obj["sme_reason"] = review.Reason;
            if (review.Action == "edit" && !string.IsNullOrEmpty(review.EditedText))
            {
                // Preserve original; surface SME edit alongside.
                if (obj.ContainsKey("claim")) { obj["original_claim"] = obj["claim"]; obj["claim"] = review.EditedText; }
                else if (obj.ContainsKey("description")) { obj["original_description"] = obj["description"]; obj["description"] = review.EditedText; }
                else if (obj.ContainsKey("question")) { obj["original_question"] = obj["question"]; obj["question"] = review.EditedText; }
            }
            return;
        }
    }

    private sealed class SignEndpointMarker;
}

/// <summary>Helpers for the canonical claim-path encoding used in B.3.</summary>
public static class ClaimPath
{
    /// <summary>Build a stable claim path: <c>$.&lt;section&gt;[?(@.id=='&lt;id&gt;')]</c>.</summary>
    public static string For(string section, string id) =>
        $"$.{section}[?(@.id=='{id}')]";

    /// <summary>Parse a claim path back into (section, id). Returns nulls on failure.</summary>
    public static (string? section, string? id) Parse(string path)
    {
        // Format: $.<section>[?(@.id=='<id>')]
        if (!path.StartsWith("$.")) return (null, null);
        var rest = path[2..];
        var bracket = rest.IndexOf('[');
        if (bracket < 0) return (null, null);
        var section = rest[..bracket];
        var idMarker = rest.IndexOf("@.id=='", StringComparison.Ordinal);
        if (idMarker < 0) return (null, null);
        var idStart = idMarker + "@.id=='".Length;
        var idEnd = rest.IndexOf("')]", idStart, StringComparison.Ordinal);
        if (idEnd < 0) return (null, null);
        return (section, rest[idStart..idEnd]);
    }
}

public sealed record RouteRequest(List<Guid>? ReviewerIds, string? RoutingNote);
public sealed record ClaimReviewRequest(string ClaimPath, string Action, string? Reason, string? EditedText);
public sealed record SignRequest(string Confirmation);
