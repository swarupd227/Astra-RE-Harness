using Astra.Api.Auth;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class MyReviewsEndpoints
{
    public static IEndpointRouteBuilder MapMyReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        // The SME landing surface.  In Phase B.3.3 the routing fan-out is single-user,
        // so "my reviews" is "every spec in IN_REVIEW / SIGNED" plus convenience counts
        // for the engineer / observer personas.
        app.MapGet("/api/v1/my-reviews", async (
            AppDbContext db,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            var specs = await db.Specs
                .AsNoTracking()
                .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile)
                    .ThenInclude(f => f!.SourceVersion).ThenInclude(v => v!.Corpus)
                .OrderByDescending(s => s.UpdatedAt)
                .ToListAsync(ct);

            var allReviews = await db.ClaimReviews.AsNoTracking().ToListAsync(ct);
            var reviewsBySpec = allReviews
                .GroupBy(r => r.SpecId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var allSignatures = await db.Signatures.AsNoTracking().ToListAsync(ct);
            var sigBySpec = allSignatures.ToDictionary(s => s.SpecId);

            var rows = specs.Select(spec =>
            {
                var doc = spec.SpecJson.RootElement;
                int countSection(string name) =>
                    doc.TryGetProperty(name, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? arr.GetArrayLength() : 0;
                var totalClaims = countSection("invariants") + countSection("side_effects")
                                  + countSection("edge_cases") + countSection("open_questions");
                var processed = reviewsBySpec.TryGetValue(spec.Id, out var rs) ? rs.Count : 0;
                var sig = sigBySpec.GetValueOrDefault(spec.Id);
                var corpus = spec.Subroutine?.SourceFile?.SourceVersion?.Corpus;

                return new
                {
                    specId = spec.Id,
                    subroutineId = spec.SubroutineId,
                    subroutineName = spec.Subroutine?.Name ?? "",
                    corpusName = corpus?.Name ?? "",
                    relativePath = spec.Subroutine?.SourceFile?.RelativePath ?? "",
                    state = spec.State,
                    updatedAt = spec.UpdatedAt,
                    routedAt = spec.UpdatedAt,
                    totalClaims,
                    processedClaims = processed,
                    invariantsCount = countSection("invariants"),
                    edgeCaseCount = countSection("edge_cases"),
                    openQuestionCount = countSection("open_questions"),
                    estimatedReviewMinutes = EstimateMinutes(totalClaims),
                    signature = sig is null ? null : new
                    {
                        signedAt = sig.SignedAt,
                        signerDisplay = sig.SignerDisplay,
                        algorithm = sig.Algorithm,
                        specCanonicalHash = sig.SpecCanonicalHash,
                    },
                };
            }).ToList();

            var awaiting = rows.Where(r => r.state == "IN_REVIEW" && r.processedClaims < r.totalClaims).ToList();
            var inProgress = rows.Where(r => r.state == "IN_REVIEW" && r.processedClaims > 0 && r.processedClaims < r.totalClaims).ToList();
            // Awaiting = no decisions yet; in-progress = some-but-not-all decisions.
            awaiting = awaiting.Where(r => r.processedClaims == 0).ToList();
            var signed = rows.Where(r => r.state == "SIGNED").ToList();

            return Results.Ok(new
            {
                persona = persona.Persona.ToString().ToLowerInvariant(),
                counts = new
                {
                    awaiting = awaiting.Count,
                    inProgress = inProgress.Count,
                    signed = signed.Count,
                },
                awaiting,
                inProgress,
                signed,
            });
        });

        return app;
    }

    private static int EstimateMinutes(int claims) =>
        // Roughly 4 minutes per claim, rounded up to the nearest 5.
        claims == 0 ? 0 : (int)Math.Ceiling(claims * 4.0 / 5.0) * 5;
}
