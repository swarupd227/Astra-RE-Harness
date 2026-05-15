using System.Globalization;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Compliance;

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
