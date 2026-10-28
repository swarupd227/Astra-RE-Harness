using Astra.Api.Evidence;

namespace Astra.Api.Endpoints;

/// <summary>
/// Read access to the evidence behind citations. A short id is only meaningful inside a version,
/// so every route is under a project and reads its current version unless one is named. All reads
/// are open to every persona: evidence is what the project already holds.
/// </summary>
public static class EvidenceChunkEndpoints
{
    private const int MaxResolve = 200;
    private const int MaxResolveDetail = 12;

    public static IEndpointRouteBuilder MapEvidenceChunkEndpoints(this IEndpointRouteBuilder app)
    {
        // Browse and search a version's evidence: ?kind=&q=&path=&limit=&offset=
        app.MapGet("/api/v1/corpora/{id:guid}/evidence", async (
            Guid id, string? kind, string? q, string? path, int? limit, int? offset, Guid? versionId,
            EvidenceService svc, CancellationToken ct) =>
        {
            var (found, version) = await svc.ResolveVersionAsync(id, versionId, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v)
                return Results.Ok(new EvidencePage(Array.Empty<EvidenceSummary>(), 0, Array.Empty<EvidenceKindCount>()));

            await svc.EnsureIndexedAsync(v, ct);
            return Results.Ok(await svc.ListAsync(v, kind, q, path, limit ?? 50, offset ?? 0, ct));
        });

        // One piece of evidence, with its text and (for line locators) the surrounding lines.
        app.MapGet("/api/v1/corpora/{id:guid}/evidence/{shortId}", async (
            Guid id, string shortId, Guid? versionId, EvidenceService svc, CancellationToken ct) =>
        {
            var normalised = EvidenceIds.Normalise(shortId);
            if (normalised is null)
                return Results.BadRequest(new { error = new { code = "evidence.invalid_id", message = $"'{shortId}' is not an evidence id (they look like SQL-3fa9c01e)." } });

            var (found, version) = await svc.ResolveVersionAsync(id, versionId, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return NotFound("evidence.not_found", $"This project has no source version yet, so {normalised} cannot be resolved.");

            await svc.EnsureIndexedAsync(v, ct);
            var detail = await svc.GetDetailAsync(v, normalised, ct);
            return detail is null
                ? NotFound("evidence.not_found", $"{normalised} is not in this version of the project. A citation can point at an earlier version, or at a file that has since been replaced.")
                : Results.Ok(detail);
        });

        // Resolve many ids at once (the chips on a page), optionally with full detail (the stacked view).
        app.MapPost("/api/v1/corpora/{id:guid}/evidence/resolve", async (
            Guid id, ResolveBody body, EvidenceService svc, CancellationToken ct) =>
        {
            var ids = body.Ids ?? Array.Empty<string>();
            if (ids.Count == 0)
                return Results.BadRequest(new { error = new { code = "evidence.no_ids", message = "Give at least one evidence id." } });
            if (ids.Count > MaxResolve)
                return Results.BadRequest(new { error = new { code = "evidence.too_many_ids", message = $"At most {MaxResolve} ids per call." } });
            if (body.Detail && ids.Count > MaxResolveDetail)
                return Results.BadRequest(new { error = new { code = "evidence.too_many_ids", message = $"At most {MaxResolveDetail} ids per call when detail is asked for." } });

            var (found, version) = await svc.ResolveVersionAsync(id, body.VersionId, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");

            var data = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (version is { } v)
            {
                await svc.EnsureIndexedAsync(v, ct);
                var wanted = ids.Select(EvidenceIds.Normalise).Where(n => n is not null).Select(n => n!).Distinct().ToList();
                if (body.Detail)
                {
                    var details = new Dictionary<string, EvidenceDetail>(StringComparer.Ordinal);
                    foreach (var w in wanted)
                        if (await svc.GetDetailAsync(v, w, ct) is { } d) details[w] = d;
                    foreach (var raw in ids)
                        data[raw] = EvidenceIds.Normalise(raw) is { } n && details.TryGetValue(n, out var hit) ? hit : null;
                }
                else
                {
                    var summaries = await svc.ResolveAsync(v, wanted, ct);
                    foreach (var raw in ids)
                        data[raw] = EvidenceIds.Normalise(raw) is { } n && summaries.TryGetValue(n, out var hit) ? hit : null;
                }
            }
            else
            {
                foreach (var raw in ids) data[raw] = null;
            }

            var missing = data.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList();
            return Results.Ok(new { data, found = data.Count - missing.Count, missing });
        });

        return app;
    }

    private sealed record ResolveBody(IReadOnlyList<string>? Ids, bool Detail = false, Guid? VersionId = null);

    private static IResult NotFound(string code, string message) =>
        Results.NotFound(new { error = new { code, message } });
}
