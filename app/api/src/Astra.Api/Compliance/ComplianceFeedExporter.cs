using System.Globalization;
using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Compliance;

/// <summary>
/// Phase #3d — compliance feed exporter.
///
/// Maps the existing append-only <see cref="AuditEvent"/> log into the
/// column shapes that downstream compliance programs (SOX, HIPAA, PCI)
/// expect. Each format is one named layout: a fixed set of column ids
/// plus, in the renderer, a row-level projection that pulls fields out
/// of the audit row + joined signature data + parsed payload.
///
/// Output is CSV-by-default for spreadsheet drop-in, JSON if the caller
/// asks for it. CSV escaping is RFC-4180-compliant (double-quote
/// surrounding, double-quote-doubled). Times are ISO-8601 with offset.
///
/// SOX is the reference shape — change-management evidence for code
/// changes is the closest analogue to what the harness produces. HIPAA
/// and PCI are documented and shipped, but their column sets are
/// adapter-tuned (a real engagement remaps source-of-truth fields to
/// the customer's evidence dictionary; this gives them a starting
/// point, not a finished feed).
/// </summary>
public sealed class ComplianceFeedExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly AppDbContext _db;
    private readonly ILogger<ComplianceFeedExporter> _log;

    public ComplianceFeedExporter(AppDbContext db, ILogger<ComplianceFeedExporter> log)
    {
        _db = db;
        _log = log;
    }

    public sealed record ExportRequest(
        string Format,
        DateTimeOffset? Since,
        DateTimeOffset? Until,
        string? Severity,
        int? Limit);

    public sealed record ExportResult(
        string Format,
        string ContentType,
        string FileName,
        string Body,
        int RowCount,
        DateTimeOffset GeneratedAt);

    // ────────────────────────────────────────────────────────────────────
    // Format catalogue — column ids + display labels per format. Renderers
    // below pull data into these column ids row-by-row.
    // ────────────────────────────────────────────────────────────────────

    public sealed record FormatDefinition(
        string Id,
        string DisplayName,
        string Description,
        IReadOnlyList<ColumnDefinition> Columns,
        string Status,
        string ContentType,
        string Extension);

    public sealed record ColumnDefinition(string Id, string Header, string Description);

    public static readonly FormatDefinition Sox = new(
        Id: "sox",
        DisplayName: "SOX · Sarbanes-Oxley change-management evidence",
        Description:
            "Append-only audit feed of every state change in the harness — extraction, sign-off, " +
            "scaffold, commit, validation. Columns are tuned for an internal-controls auditor: " +
            "who-did-what-when-against-which-source-revision, with cryptographic hashes for " +
            "tamper-evidence. Drops straight into Workiva / AuditBoard / SailPoint evidence stores.",
        Status: "production",
        ContentType: "text/csv; charset=utf-8",
        Extension: "csv",
        Columns: new[]
        {
            new ColumnDefinition("timestamp", "Timestamp", "ISO-8601 with offset, UTC."),
            new ColumnDefinition("event_id", "Event ID", "Immutable audit-row id (UUID)."),
            new ColumnDefinition("event_type", "Event Type", "Hierarchical event name — e.g. spec.signed."),
            new ColumnDefinition("severity", "Severity", "Derived: critical / high / medium / low / info."),
            new ColumnDefinition("actor_persona", "Actor Persona", "engineer · sme · system."),
            new ColumnDefinition("actor_display", "Actor", "Human-readable signer/operator name."),
            new ColumnDefinition("target_type", "Target Type", "spec · subroutine · scaffold · corpus."),
            new ColumnDefinition("target_id", "Target ID", "UUID of the target row."),
            new ColumnDefinition("subject", "Subject", "Denormalised label — e.g. subroutine name or scaffold path."),
            new ColumnDefinition("source_revision_hash", "Source Revision Hash", "sha256 of the source file at sign time (signature.source_version_hash)."),
            new ColumnDefinition("spec_canonical_hash", "Spec Canonical Hash", "sha256 of the canonical-JSON-encoded signed spec."),
            new ColumnDefinition("signature_key_id", "Signature Key ID", "Signing key identifier (HSM key reference)."),
            new ColumnDefinition("validation_bypassed", "Validation Bypassed", "true when commit overrode the validation gate."),
            new ColumnDefinition("ip_address", "IP Address", "Client IP at action time."),
            new ColumnDefinition("user_agent", "User Agent", "Browser/CLI fingerprint."),
            new ColumnDefinition("payload_excerpt", "Payload Excerpt", "JSON-encoded key fields from the audit payload."),
        });

    public static readonly FormatDefinition Hipaa = new(
        Id: "hipaa",
        DisplayName: "HIPAA · Privacy / Security Rule access log",
        Description:
            "PHI-style access feed. The harness operates on source code, not PHI, so the column " +
            "mapping is provided as a starting point for healthcare-adjacent engagements where " +
            "the audit feed must drop into a security analytics platform (Splunk ES, Sentinel, " +
            "Imperva). Replace `subject_id` mapping per engagement.",
        Status: "preview · engagement-tuned",
        ContentType: "text/csv; charset=utf-8",
        Extension: "csv",
        Columns: new[]
        {
            new ColumnDefinition("accessed_at", "Accessed At", "ISO-8601 with offset."),
            new ColumnDefinition("event_id", "Event ID", "Immutable audit-row id."),
            new ColumnDefinition("user_id", "User ID", "Persona-prefixed identifier."),
            new ColumnDefinition("user_role", "User Role", "engineer · sme · system."),
            new ColumnDefinition("action", "Action", "READ · WRITE · SIGN · COMMIT · EXPORT."),
            new ColumnDefinition("resource", "Resource", "Denormalised target identifier."),
            new ColumnDefinition("subject_id", "Subject ID", "Engagement-mapped (default: target_id)."),
            new ColumnDefinition("result", "Result", "success · failure."),
            new ColumnDefinition("client_ip", "Client IP", "Network origin."),
            new ColumnDefinition("justification", "Justification", "Human-readable rationale from the payload."),
        });

    public static readonly FormatDefinition Pci = new(
        Id: "pci",
        DisplayName: "PCI-DSS · Cardholder-data-environment audit feed",
        Description:
            "PCI 10.x audit log fields. Like HIPAA, the harness doesn't itself process " +
            "cardholder data; the format is provided for in-scope migration engagements where " +
            "the customer's CDE pipeline expects a unified audit feed. Severity, system-component, " +
            "and data-classification columns are flagged conservatively to avoid false-positive " +
            "high-severity events.",
        Status: "preview · engagement-tuned",
        ContentType: "text/csv; charset=utf-8",
        Extension: "csv",
        Columns: new[]
        {
            new ColumnDefinition("timestamp", "Timestamp", "ISO-8601 with offset."),
            new ColumnDefinition("event_id", "Event ID", "Immutable audit-row id."),
            new ColumnDefinition("user", "User", "Persona · display name."),
            new ColumnDefinition("event", "Event", "Hierarchical event name."),
            new ColumnDefinition("result", "Result", "success · failure."),
            new ColumnDefinition("system_component", "System Component", "astra-api · astra-worker · astra-parser."),
            new ColumnDefinition("data_classification", "Data Classification", "internal-source-code (harness default)."),
            new ColumnDefinition("source_address", "Source Address", "Client IP."),
            new ColumnDefinition("affected_resource", "Affected Resource", "Target type + id."),
            new ColumnDefinition("integrity_hash", "Integrity Hash", "Signed-spec canonical hash where applicable."),
        });

    public static readonly IReadOnlyList<FormatDefinition> Formats = new[] { Sox, Hipaa, Pci };

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    public static FormatDefinition? FindFormat(string id) =>
        Formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task<ExportResult> ExportAsync(ExportRequest req, CancellationToken ct)
    {
        var format = FindFormat(req.Format)
            ?? throw new ArgumentException($"Unknown compliance format '{req.Format}'.");

        // Pull events + denormalised labels in a single roundtrip.
        var q = _db.AuditEvents.AsNoTracking().AsQueryable();
        if (req.Since is not null) q = q.Where(e => e.OccurredAt >= req.Since.Value);
        if (req.Until is not null) q = q.Where(e => e.OccurredAt < req.Until.Value);
        q = q.OrderBy(e => e.OccurredAt);
        if (req.Limit is > 0) q = q.Take(req.Limit.Value);

        var events = await q.ToListAsync(ct);

        // For events whose target is a spec or scaffold, fetch the related
        // signature / spec / scaffold so we can denormalise the subject
        // label and integrity hashes. One bulk query per type keeps it cheap.
        var specIds = events.Where(e => e.TargetType == "spec" && e.TargetId.HasValue)
            .Select(e => e.TargetId!.Value).Distinct().ToHashSet();
        var scaffoldIds = events.Where(e => e.TargetType == "scaffold" && e.TargetId.HasValue)
            .Select(e => e.TargetId!.Value).Distinct().ToHashSet();
        var subroutineIds = events.Where(e => e.TargetType == "subroutine" && e.TargetId.HasValue)
            .Select(e => e.TargetId!.Value).Distinct().ToHashSet();

        var specToSubName = await _db.Specs.AsNoTracking()
            .Where(s => specIds.Contains(s.Id))
            .Include(s => s.Subroutine)
            .ToDictionaryAsync(s => s.Id, s => s.Subroutine?.Name ?? "", ct);
        var signaturesBySpec = await _db.Signatures.AsNoTracking()
            .Where(sig => specIds.Contains(sig.SpecId))
            .ToDictionaryAsync(sig => sig.SpecId, ct);
        var scaffoldToSpec = await _db.Scaffolds.AsNoTracking()
            .Where(s => scaffoldIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.SpecId, ct);
        var subroutineNames = await _db.Subroutines.AsNoTracking()
            .Where(s => subroutineIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var rows = new List<Dictionary<string, string>>(events.Count);
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(req.Severity) &&
                !string.Equals(MapSeverity(e.EventType), req.Severity, StringComparison.OrdinalIgnoreCase))
                continue;

            // Resolve denormalised subject + integrity hashes.
            string subject = "";
            string sourceRevisionHash = "";
            string specCanonicalHash = "";
            string signatureKeyId = "";

            if (e.TargetType == "spec" && e.TargetId.HasValue)
            {
                subject = specToSubName.GetValueOrDefault(e.TargetId.Value) ?? "";
                if (signaturesBySpec.TryGetValue(e.TargetId.Value, out var sig))
                {
                    sourceRevisionHash = sig.SourceVersionHash;
                    specCanonicalHash = sig.SpecCanonicalHash;
                    signatureKeyId = sig.SignatureKeyId;
                }
            }
            else if (e.TargetType == "scaffold" && e.TargetId.HasValue
                  && scaffoldToSpec.TryGetValue(e.TargetId.Value, out var derivedSpecId))
            {
                subject = specToSubName.GetValueOrDefault(derivedSpecId) ?? "";
                if (signaturesBySpec.TryGetValue(derivedSpecId, out var sig))
                {
                    sourceRevisionHash = sig.SourceVersionHash;
                    specCanonicalHash = sig.SpecCanonicalHash;
                    signatureKeyId = sig.SignatureKeyId;
                }
            }
            else if (e.TargetType == "subroutine" && e.TargetId.HasValue)
            {
                subject = subroutineNames.GetValueOrDefault(e.TargetId.Value) ?? "";
            }

            // Pull bypass + payload excerpt from the event payload.
            var (validationBypassed, payloadExcerpt) = ExtractPayloadHighlights(e);

            var row = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timestamp"] = e.OccurredAt.ToString("o", CultureInfo.InvariantCulture),
                ["accessed_at"] = e.OccurredAt.ToString("o", CultureInfo.InvariantCulture),
                ["event_id"] = e.Id.ToString(),
                ["event_type"] = e.EventType,
                ["event"] = e.EventType,
                ["severity"] = MapSeverity(e.EventType),
                ["actor_persona"] = e.ActorPersona,
                ["actor_display"] = e.ActorDisplay,
                ["user_id"] = $"{e.ActorPersona}:{e.ActorId?.ToString() ?? "system"}",
                ["user_role"] = e.ActorPersona,
                ["user"] = $"{e.ActorPersona}:{e.ActorDisplay}",
                ["action"] = MapAction(e.EventType),
                ["result"] = MapResult(e.EventType),
                ["target_type"] = e.TargetType,
                ["target_id"] = e.TargetId?.ToString() ?? "",
                ["subject"] = subject,
                ["resource"] = $"{e.TargetType}:{e.TargetId}",
                ["affected_resource"] = $"{e.TargetType}:{e.TargetId}",
                ["subject_id"] = e.TargetId?.ToString() ?? "",
                ["source_revision_hash"] = sourceRevisionHash,
                ["spec_canonical_hash"] = specCanonicalHash,
                ["signature_key_id"] = signatureKeyId,
                ["integrity_hash"] = !string.IsNullOrEmpty(specCanonicalHash) ? specCanonicalHash : sourceRevisionHash,
                ["validation_bypassed"] = validationBypassed,
                ["ip_address"] = e.IpAddress ?? "",
                ["client_ip"] = e.IpAddress ?? "",
                ["source_address"] = e.IpAddress ?? "",
                ["user_agent"] = e.UserAgent ?? "",
                ["system_component"] = "astra-api",
                ["data_classification"] = "internal-source-code",
                ["justification"] = payloadExcerpt,
                ["payload_excerpt"] = payloadExcerpt,
            };
            rows.Add(row);
        }

        var body = RenderCsv(format, rows);
        var now = DateTimeOffset.UtcNow;
        var fname = $"compliance-{format.Id}-{now:yyyyMMdd'T'HHmmss'Z'}.{format.Extension}";

        _log.LogInformation(
            "Exported {Rows} rows for compliance format {Format} (since={Since} until={Until})",
            rows.Count, format.Id, req.Since, req.Until);

        return new ExportResult(
            Format: format.Id,
            ContentType: format.ContentType,
            FileName: fname,
            Body: body,
            RowCount: rows.Count,
            GeneratedAt: now);
    }

    // ────────────────────────────────────────────────────────────────────
    // Mappers
    // ────────────────────────────────────────────────────────────────────

    private static string MapSeverity(string eventType) => eventType switch
    {
        "spec.signed"                    => "critical",
        "scaffold.committed"             => "critical",
        // Phase 9.3.b — the 4th-gate "failed" verdict means the property-test
        // sidecar found a real falsifying counterexample. That's a genuine bug
        // discovered against a signed contract; rank it next to spec.superseded.
        "validation.falsifying.failed"   => "high",
        "spec.superseded"                => "high",
        "claim.reject"                   => "high",
        "validation.completed"           => "medium",
        "spec.extracted"                 => "medium",
        "claim.accept"                   => "medium",
        "spec.routed"                    => "low",
        "scaffold.generated"             => "low",
        "test_pack.generated"            => "low",
        // 4th-gate clean pass + start are informational — they show the gate
        // ran but don't represent a policy-relevant outcome on their own.
        "validation.falsifying.passed"   => "low",
        "validation.falsifying.started"  => "info",
        "corpus.ingested"                => "info",
        "source.parsed"                  => "info",
        _                                => "info",
    };

    private static string MapAction(string eventType)
    {
        // The HIPAA / PCI columns want READ / WRITE / SIGN / COMMIT / EXPORT.
        // Map our event-type names accordingly so security-platform filters fire.
        if (eventType.EndsWith(".signed", StringComparison.Ordinal)) return "SIGN";
        if (eventType.EndsWith(".committed", StringComparison.Ordinal)) return "COMMIT";
        if (eventType.Contains("compliance.feed_exported", StringComparison.Ordinal)) return "EXPORT";
        if (eventType.StartsWith("validation.", StringComparison.Ordinal)) return "VALIDATE";
        if (eventType.StartsWith("claim.", StringComparison.Ordinal)) return "WRITE";
        if (eventType.EndsWith(".extracted", StringComparison.Ordinal) ||
            eventType.EndsWith(".generated", StringComparison.Ordinal) ||
            eventType.EndsWith(".routed", StringComparison.Ordinal) ||
            eventType.EndsWith(".superseded", StringComparison.Ordinal) ||
            eventType.EndsWith(".carried_forward", StringComparison.Ordinal) ||
            eventType.EndsWith(".reingested", StringComparison.Ordinal) ||
            eventType.EndsWith(".ingested", StringComparison.Ordinal) ||
            eventType.EndsWith(".parsed", StringComparison.Ordinal))
            return "WRITE";
        return "READ";
    }

    private static string MapResult(string eventType) =>
        // Both naming conventions exist in the codebase: snake-style
        // `_failed` (corpus.ingest_failed, compile.build_failed) and
        // dotted-style `.failed` (harmonisation.failed,
        // validation.falsifying.failed). Both are honest failure signals
        // and must be reported as such in the compliance feed.
        eventType.EndsWith("_failed", StringComparison.OrdinalIgnoreCase) ||
        eventType.EndsWith(".failed", StringComparison.OrdinalIgnoreCase)
            ? "failure" : "success";

    private static (string ValidationBypassed, string PayloadExcerpt) ExtractPayloadHighlights(AuditEvent e)
    {
        string bypassed = "";
        var keys = new List<string>();
        try
        {
            var root = e.Payload.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("validationBypassed", out var vb))
                    bypassed = vb.ValueKind == JsonValueKind.True ? "true"
                            : vb.ValueKind == JsonValueKind.False ? "false"
                            : vb.ToString();
                // A short JSON excerpt — first ~360 chars of the serialised
                // payload. The full payload remains queryable via the audit
                // endpoint; this column is a quick-glance excerpt for the
                // SOX / HIPAA grid.
                var json = JsonSerializer.Serialize(root, JsonOpts);
                if (json.Length > 360) json = json.Substring(0, 357) + "...";
                return (bypassed, json);
            }
        }
        catch
        {
            // Defensive — corrupted payload should never break the export.
        }
        return (bypassed, "");
    }

    // ────────────────────────────────────────────────────────────────────
    // CSV renderer (RFC 4180)
    // ────────────────────────────────────────────────────────────────────

    private static string RenderCsv(FormatDefinition format, IReadOnlyList<Dictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", format.Columns.Select(c => Escape(c.Header))));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", format.Columns.Select(c => Escape(row.GetValueOrDefault(c.Id) ?? ""))));
        }
        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool needsQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
