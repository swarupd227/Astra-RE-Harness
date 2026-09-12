using Astra.Api.Auth;
using Astra.Api.Persistence.Entities;
using Astra.Api.Signing;
using Astra.Api.Specs;

namespace Astra.Api.Endpoints;

/// <summary>
/// B.3/B.4 spec review surface. The bodies moved to
/// <see cref="SpecReviewService"/> (WS2) so the copilot's route / review /
/// sign tools share one code path with these buttons; the endpoints keep
/// the persona checks and the HTTP shapes exactly as before.
/// </summary>
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
            SpecReviewService service,
            DevPersonaContext persona,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Engineer)
                return Forbid("auth.engineer_required", "Only engineers can route a spec to review.");

            var (ok, error) = await service.RouteAsync(id, body, persona, ctx, ct);
            return error is not null ? FromError(error) : Results.Ok(ok);
        });

        // ─── Claim-level SME action ──────────────────────────────────────
        app.MapPost("/api/v1/specs/{id:guid}/claims/review", async (
            Guid id,
            ClaimReviewRequest body,
            SpecReviewService service,
            DevPersonaContext persona,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var (review, error) = await service.ReviewClaimAsync(id, body, persona, ctx, ct);
            return error is not null ? FromError(error) : Results.Ok(ToDto(review!));
        });

        // ─── Sign the spec ───────────────────────────────────────────────
        app.MapPost("/api/v1/specs/{id:guid}/sign", async (
            Guid id,
            SignRequest body,
            SpecReviewService service,
            DevPersonaContext persona,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Sme)
                return Forbid("auth.sme_required", "Only SMEs can sign a spec.");

            var (outcome, error) = await service.SignAsync(id, body.Confirmation, persona, ctx, ct);
            return error is not null
                ? FromError(error)
                : Results.Ok(SignatureToDto(outcome!.Signature, outcome.SpecState, outcome.Idempotent));
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

    private static IResult FromError(SpecReviewError e) => e.Status switch
    {
        404 => Results.NotFound(new { error = new { code = e.Code } }),
        403 => Forbid(e.Code, e.Message),
        _ => e.Details is null
            ? Results.BadRequest(new { error = new { code = e.Code, message = e.Message } })
            : Results.BadRequest(new { error = new { code = e.Code, message = e.Message, details = e.Details } }),
    };

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
