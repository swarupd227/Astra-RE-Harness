using System.Globalization;
using Astra.Api.Audit;
using Astra.Api.Auth;
using System.Text.Json;
using Astra.Api.Compliance;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #3d — compliance feed exporter surface.
///
///   GET /api/v1/compliance/formats                            list supported formats
///   GET /api/v1/compliance/feed?format=sox|hipaa|pci&since=&until=&severity=
///                                                             download a CSV (or json)
///
/// The export endpoint also writes a <c>compliance.feed_exported</c>
/// audit row so the export itself is in the audit trail — meta-evidence
/// that the auditor pulled the feed at time T with parameters P.
/// </summary>
public static class ComplianceEndpoints
{
    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/compliance/formats", () =>
        {
            return Results.Ok(new
            {
                data = ComplianceFeedExporter.Formats.Select(f => new
                {
                    id = f.Id,
                    displayName = f.DisplayName,
                    description = f.Description,
                    status = f.Status,
                    contentType = f.ContentType,
                    extension = f.Extension,
                    columnCount = f.Columns.Count,
                    columns = f.Columns.Select(c => new
                    {
                        id = c.Id,
                        header = c.Header,
                        description = c.Description,
                    }),
                }),
            });
        });

        // GET /api/v1/compliance/summary?since=&until= — what an auditor asks first, for the Compliance page's
        // overview strip: how many events in the range, sign-offs, commits, commits that bypassed a failed
        // validation gate, exports made, and the busiest event types.
        app.MapGet("/api/v1/compliance/summary", async (string? since, string? until, AppDbContext db, CancellationToken ct) =>
        {
            var sinceTs = ParseTimestamp(since);
            var untilTs = ParseTimestamp(until);
            var q = db.AuditEvents.AsNoTracking().AsQueryable();
            if (sinceTs is { } s) q = q.Where(e => e.OccurredAt >= s);
            if (untilTs is { } u) q = q.Where(e => e.OccurredAt <= u);

            var byType = await q.GroupBy(e => e.EventType)
                .Select(g => new { type = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToListAsync(ct);
            int Count(string t) => byType.Where(x => x.type == t).Sum(x => x.count);

            // Bypasses live in the payload; commit events are few, so read them and check in memory.
            var commits = await q.Where(e => e.EventType.Contains("commit"))
                .Select(e => e.Payload)
                .ToListAsync(ct);
            var bypasses = commits.Count(p =>
                p.RootElement.ValueKind == JsonValueKind.Object
                && p.RootElement.TryGetProperty("validationBypassed", out var vb)
                && vb.ValueKind == JsonValueKind.True);

            return Results.Ok(new
            {
                total = byType.Sum(x => x.count),
                signed = Count("spec.signed"),
                committed = commits.Count,
                validationBypassed = bypasses,
                exports = Count("compliance.feed_exported"),
                byType = byType.Take(12),
            });
        });

        // GET /api/v1/compliance/feed?format=sox&since=2026-05-01T00:00:00Z&until=...
        // Engineer + auditor personas both allowed; SME is allowed since the
        // SME is the one who actually signed and may need to evidence it.
        app.MapGet("/api/v1/compliance/feed", async (
            string? format,
            string? since,
            string? until,
            string? severity,
            int? limit,
            ComplianceFeedExporter exporter,
            IAuditLogger audit,
            DevPersonaContext persona,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var formatId = format ?? "sox";
            DateTimeOffset? sinceTs = ParseTimestamp(since);
            DateTimeOffset? untilTs = ParseTimestamp(until);
            try
            {
                var result = await exporter.ExportAsync(
                    new ComplianceFeedExporter.ExportRequest(formatId, sinceTs, untilTs, severity, limit),
                    ct);

                // Meta-audit: record the export itself.
                await audit.LogAsync(
                    "compliance.feed_exported", "compliance", null, persona,
                    payload: new
                    {
                        format = result.Format,
                        rowCount = result.RowCount,
                        since = sinceTs?.ToString("o", CultureInfo.InvariantCulture),
                        until = untilTs?.ToString("o", CultureInfo.InvariantCulture),
                        severity,
                        fileName = result.FileName,
                    },
                    ctx, ct);

                ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{result.FileName}\"";
                ctx.Response.Headers["X-Astra-Row-Count"] = result.RowCount.ToString(CultureInfo.InvariantCulture);
                ctx.Response.Headers["X-Astra-Format"] = result.Format;
                return Results.Text(result.Body, result.ContentType);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { code = "compliance.invalid_format", message = ex.Message } });
            }
        });

        return app;
    }

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return t;
        return null;
    }
}
