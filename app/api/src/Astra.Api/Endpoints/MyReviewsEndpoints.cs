using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Specs;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class MyReviewsEndpoints
{
    public static IEndpointRouteBuilder MapMyReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        // The SME landing surface.  In Phase B.3.3 the routing fan-out is single-user,
        // so "my reviews" is "every spec in IN_REVIEW / SIGNED" plus convenience counts
        // for the engineer / observer personas.
        // Who can be assigned a review: people in the Users directory who review (SME) or administer.
        app.MapGet("/api/v1/reviewers", async (ReviewAssignmentService assign, CancellationToken ct) =>
            Results.Ok(new { data = (await assign.ReviewersAsync(ct)).Select(r => new { id = r.Id, displayName = r.DisplayName }) }));

        // Assign a spec's review to one person, or clear it (userId null). Engineer / Admin; audited.
        app.MapPost("/api/v1/specs/{id:guid}/assign", async (
            Guid id, AssignReviewRequest body, ReviewAssignmentService assign, DevPersonaContext actor,
            HttpContext http, CancellationToken ct) =>
        {
            var (who, error) = await assign.AssignAsync(id, body.UserId, actor, http, ct);
            if (error is not null)
                return Results.Json(new { error = new { code = error.Code, message = error.Message } }, statusCode: error.Status);
            return Results.Ok(new { specId = id, assignee = who is null ? null : new { id = who.Id, displayName = who.DisplayName } });
        });

        app.MapGet("/api/v1/my-reviews", async (
            AppDbContext db,
            DevPersonaContext persona,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Only the specs this page lists (in review or signed), and claim decisions counted in the database —
            // it used to load every spec and every claim review on each request.
            var specs = await db.Specs
                .AsNoTracking()
                .Where(s => s.State == "IN_REVIEW" || s.State == "SIGNED")
                .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile)
                    .ThenInclude(f => f!.SourceVersion).ThenInclude(v => v!.Corpus)
                .OrderByDescending(s => s.UpdatedAt)
                .ToListAsync(ct);
            var specIds = specs.Select(s => s.Id).ToList();

            var reviewCounts = await db.ClaimReviews.AsNoTracking()
                .Where(r => specIds.Contains(r.SpecId))
                .GroupBy(r => r.SpecId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            var sigBySpec = await db.Signatures.AsNoTracking()
                .Where(s => specIds.Contains(s.SpecId))
                .ToDictionaryAsync(s => s.SpecId, ct);

            var assigneeIds = specs.Where(s => s.AssignedUserId != null).Select(s => s.AssignedUserId!.Value).Distinct().ToList();
            var assignees = await db.Users.AsNoTracking().Where(u => assigneeIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var me = await MeAsync(db, http, ct);

            var rows = specs.Select(spec =>
            {
                var doc = spec.SpecJson.RootElement;
                int countSection(string name) =>
                    doc.TryGetProperty(name, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? arr.GetArrayLength() : 0;
                var totalClaims = countSection("invariants") + countSection("side_effects")
                                  + countSection("edge_cases") + countSection("open_questions");
                var processed = reviewCounts.GetValueOrDefault(spec.Id);
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
                    assignee = spec.AssignedUserId is { } aid && assignees.TryGetValue(aid, out var aname)
                        ? new { id = aid, displayName = aname } : null,
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
                // The signed-in person as a Users-directory entry, when there is one (password sign-in); null with the
                // shared dev personas, where "assigned to me" has no meaning.
                me,
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

    /// <summary>
    /// The signed-in user (password mode puts it in <c>HttpContext.Items["user"]</c>) matched to the Users directory by
    /// e-mail or display name — the directory is where reviews are assigned.
    /// </summary>
    private static async Task<object?> MeAsync(AppDbContext db, HttpContext http, CancellationToken ct)
    {
        if (http.Items["user"] is not AppUser signedIn) return null;
        var key = signedIn.Username.ToLower();
        var name = signedIn.DisplayName.ToLower();
        var match = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email.ToLower() == key || u.DisplayName.ToLower() == name, ct);
        return match is null ? null : new { id = match.Id, displayName = match.DisplayName };
    }

    private static int EstimateMinutes(int claims) =>
        // Roughly 4 minutes per claim, rounded up to the nearest 5.
        claims == 0 ? 0 : (int)Math.Ceiling(claims * 4.0 / 5.0) * 5;
}

public sealed record AssignReviewRequest(Guid? UserId);
