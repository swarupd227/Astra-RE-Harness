using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Endpoints;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Signing;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Specs;

/// <summary>A service-level failure the caller maps to an HTTP status (the
/// REST endpoints) or to a tool error (the copilot).</summary>
public sealed record SpecReviewError(int Status, string Code, string Message, object? Details = null);

/// <summary>
/// Route → claim review → sign, extracted verbatim from the B.3/B.4 endpoint
/// bodies so the copilot's <c>route_for_review</c> / <c>review_claim</c> /
/// <c>sign_spec</c> tools run the same code path (same preconditions, same
/// idempotency, same signing manifest, same audit rows) as the buttons.
/// Persona checks stay with the caller: both the endpoint and the tool
/// registry enforce them before calling in.
/// </summary>
public sealed class SpecReviewService
{
    private readonly AppDbContext _db;
    private readonly IHsmSigner _signer;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<SpecReviewService> _log;

    public SpecReviewService(
        AppDbContext db, IHsmSigner signer, IBlobClient blob, StorageOptions storage,
        IAuditLogger audit, ILogger<SpecReviewService> log)
    {
        _db = db;
        _signer = signer;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _log = log;
    }

    // ── Route ────────────────────────────────────────────────────────────

    public async Task<(object? Ok, SpecReviewError? Error)> RouteAsync(
        Guid specId, RouteRequest body, DevPersonaContext persona, HttpContext? ctx, CancellationToken ct)
    {
        var spec = await _db.Specs.Include(s => s.Subroutine).FirstOrDefaultAsync(s => s.Id == specId, ct);
        if (spec is null) return (null, new(404, "spec.not_found", "Spec not found."));

        if (spec.State != "DRAFT")
            return (null, new(400, "spec.invalid_state", $"Cannot route from state {spec.State}."));

        spec.State = "IN_REVIEW";
        spec.UpdatedAt = DateTimeOffset.UtcNow;
        if (spec.Subroutine is not null) spec.Subroutine.State = "IN_REVIEW";
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
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

        return (new { id = spec.Id, state = spec.State, routingNote = body.RoutingNote }, null);
    }

    // ── Claim review ─────────────────────────────────────────────────────

    public async Task<(ClaimReview? Ok, SpecReviewError? Error)> ReviewClaimAsync(
        Guid specId, ClaimReviewRequest body, DevPersonaContext persona, HttpContext? ctx, CancellationToken ct)
    {
        var spec = await _db.Specs.FirstOrDefaultAsync(s => s.Id == specId, ct);
        if (spec is null) return (null, new(404, "spec.not_found", "Spec not found."));
        if (spec.State != "IN_REVIEW")
            return (null, new(400, "spec.invalid_state", $"Claim review requires IN_REVIEW state (was {spec.State})."));

        if (string.IsNullOrWhiteSpace(body.ClaimPath))
            return (null, new(400, "claim.path_required", "claimPath is required."));
        if (body.Action is not ("accept" or "edit" or "reject" or "question"))
            return (null, new(400, "claim.invalid_action", "action must be accept | edit | reject | question."));
        if (body.Action == "edit" && string.IsNullOrWhiteSpace(body.EditedText))
            return (null, new(400, "claim.edited_text_required", "edited_text required for edit action."));
        if (body.Action == "reject" && (body.Reason is null || body.Reason.Length < 20))
            return (null, new(400, "claim.reason_too_short", "Reject reason must be ≥ 20 characters."));
        if (body.Action == "question" && string.IsNullOrWhiteSpace(body.Reason))
            return (null, new(400, "claim.question_body_required", "question reason (body) is required."));

        var existing = await _db.ClaimReviews
            .FirstOrDefaultAsync(r => r.SpecId == specId && r.ClaimPath == body.ClaimPath, ct);
        var now = DateTimeOffset.UtcNow;

        ClaimReview entity;
        string? previousAction = existing?.Action;
        if (existing is null)
        {
            entity = new ClaimReview
            {
                Id = Guid.NewGuid(),
                SpecId = specId,
                ClaimPath = body.ClaimPath,
                Action = body.Action,
                Reason = body.Reason,
                EditedText = body.EditedText,
                ReviewedAt = now,
            };
            await _db.ClaimReviews.AddAsync(entity, ct);
        }
        else
        {
            existing.Action = body.Action;
            existing.Reason = body.Reason;
            existing.EditedText = body.EditedText;
            existing.ReviewedAt = now;
            entity = existing;
        }
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            $"claim.{body.Action}",
            "spec", specId,
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

        return (entity, null);
    }

    // ── Sign ─────────────────────────────────────────────────────────────

    public sealed record SignOutcome(Signature Signature, string SpecState, bool Idempotent);

    public async Task<(SignOutcome? Ok, SpecReviewError? Error)> SignAsync(
        Guid specId, string? confirmation, DevPersonaContext persona, HttpContext? ctx, CancellationToken ct)
    {
        var spec = await _db.Specs
            .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile)
            .FirstOrDefaultAsync(s => s.Id == specId, ct);
        if (spec is null) return (null, new(404, "spec.not_found", "Spec not found."));

        // Idempotency fast-path + self-heal of a stuck IN_REVIEW row.
        var existingSig = await _db.Signatures.FirstOrDefaultAsync(s => s.SpecId == specId, ct);
        if (existingSig is not null)
        {
            if (spec.State != "SIGNED")
            {
                _log.LogWarning(
                    "Self-healing stuck spec {SpecId}: signature exists but state was {State}",
                    spec.Id, spec.State);
                spec.State = "SIGNED";
                spec.UpdatedAt = DateTimeOffset.UtcNow;
                if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";
                await _db.SaveChangesAsync(ct);
            }
            return (new SignOutcome(existingSig, spec.State, true), null);
        }

        if (spec.State != "IN_REVIEW")
            return (null, new(400, "spec.invalid_state", $"Sign requires IN_REVIEW state (was {spec.State})."));

        var preconditionFailures = await CheckPreconditionsAsync(spec, ct);
        if (preconditionFailures.Count > 0)
            return (null, new(400, "spec.sign.preconditions_unmet",
                "Sign-off requires every claim to be processed and every open question resolved.",
                preconditionFailures));

        if (string.IsNullOrWhiteSpace(confirmation) ||
            !confirmation.Contains("I have reviewed every claim", StringComparison.OrdinalIgnoreCase))
            return (null, new(400, "spec.sign.confirmation_required", "The canonical confirmation sentence is required."));

        var reviews = await _db.ClaimReviews.Where(r => r.SpecId == specId).ToListAsync(ct);
        var withReviews = ApplyReviews(spec.SpecJson, reviews);
        var canonical = CanonicalJson.Serialize(withReviews.RootElement);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        var canonicalHash = "sha256:" + Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        var sourceVersionHash = "sha256:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(spec.SourceVersionId.ToString()))).ToLowerInvariant();

        var signatureBytes = await _signer.SignAsync(canonicalBytes, ct);

        var signedManifest = new Dictionary<string, object?>
        {
            ["manifestVersion"] = 1,
            ["specId"] = spec.Id,
            ["subroutineId"] = spec.SubroutineId,
            ["sourceVersionId"] = spec.SourceVersionId,
            ["sourceVersionHash"] = sourceVersionHash,
            ["specCanonicalHash"] = canonicalHash,
            ["algorithm"] = _signer.Algorithm,
            ["keyId"] = _signer.ActiveKeyId,
            ["signatureBase64"] = Convert.ToBase64String(signatureBytes),
            ["signedAt"] = DateTimeOffset.UtcNow,
            ["signerDisplay"] = persona.DisplayName,
            ["specCanonical"] = canonical,
            ["spec"] = JsonDocument.Parse(canonical).RootElement,
        };
        var manifestJson = JsonSerializer.Serialize(signedManifest);
        var blobUri = await _blob.PutTextAsync(
            _storage.Buckets.SignedSpecs,
            $"{spec.Id}/{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json",
            manifestJson,
            "application/json",
            ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var raceWinner = await _db.Signatures.FirstOrDefaultAsync(s => s.SpecId == specId, ct);
        if (raceWinner is not null)
        {
            if (spec.State != "SIGNED")
            {
                spec.State = "SIGNED";
                spec.UpdatedAt = DateTimeOffset.UtcNow;
                if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return (new SignOutcome(raceWinner, spec.State, true), null);
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
            SignatureKeyId = _signer.ActiveKeyId,
            Algorithm = _signer.Algorithm,
            SignedBlobUri = blobUri,
        };
        await _db.Signatures.AddAsync(sig, ct);

        spec.State = "SIGNED";
        spec.UpdatedAt = DateTimeOffset.UtcNow;
        spec.SpecJson = withReviews;
        if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            var winner = await _db.Signatures
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpecId == specId, ct);
            if (winner is null) throw;
            var freshSpec = await _db.Specs
                .Include(s => s.Subroutine)
                .FirstAsync(s => s.Id == specId, ct);
            if (freshSpec.State != "SIGNED")
            {
                freshSpec.State = "SIGNED";
                freshSpec.UpdatedAt = DateTimeOffset.UtcNow;
                if (freshSpec.Subroutine is not null) freshSpec.Subroutine.State = "SIGNED";
                await _db.SaveChangesAsync(ct);
            }
            return (new SignOutcome(winner, "SIGNED", true), null);
        }

        _log.LogInformation(
            "Signed spec {SpecId} key={KeyId} hash={Hash} blob={BlobUri}",
            spec.Id, sig.SignatureKeyId, sig.SpecCanonicalHash, sig.SignedBlobUri);

        await _audit.LogAsync(
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

        return (new SignOutcome(sig, spec.State, false), null);
    }

    /// <summary>Unreviewed / unresolved claims — what still blocks a sign-off.</summary>
    public async Task<List<object>> CheckPreconditionsAsync(Spec spec, CancellationToken ct)
    {
        var failures = new List<object>();
        var reviews = await _db.ClaimReviews
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
        check("side_effects", "id");
        check("edge_cases", "id");
        check("open_questions", "id");
        return failures;
    }

    private static JsonDocument ApplyReviews(JsonDocument original, List<ClaimReview> reviews)
    {
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
                if (obj.ContainsKey("claim")) { obj["original_claim"] = obj["claim"]; obj["claim"] = review.EditedText; }
                else if (obj.ContainsKey("description")) { obj["original_description"] = obj["description"]; obj["description"] = review.EditedText; }
                else if (obj.ContainsKey("question")) { obj["original_question"] = obj["question"]; obj["question"] = review.EditedText; }
            }
            return;
        }
    }
}
