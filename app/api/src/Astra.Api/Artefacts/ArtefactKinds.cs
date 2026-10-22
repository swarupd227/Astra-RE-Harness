using Astra.Api.Ingest;

namespace Astra.Api.Artefacts;

/// <summary>
/// How much a statement drawn from this kind of evidence can be trusted on its own.
/// Parser-derived facts are <see cref="Structured"/>; what people said or wrote is
/// <see cref="Narrative"/> and must be checked against structured facts before it is
/// treated as supported.
/// </summary>
public enum TrustTier { Structured, Narrative }

/// <summary>
/// One kind of artefact a project can ingest. A kind is data (this record) plus an
/// adapter that turns its files into evidence; adding a kind never touches the parser
/// sidecar. <see cref="Core"/> kinds are always on and cannot be switched off.
/// </summary>
public sealed record ArtefactKind(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<string> Extensions,
    TrustTier Trust,
    long MaxBytes,
    bool IsText,
    bool Core = false);

public static class ArtefactKinds
{
    public const string Code = "code";
    public const string Sql = "sql";
    public const string Job = "job";
    public const string Etl = "etl";
    public const string Config = "config";
    public const string Transcript = "transcript";
    public const string Ticket = "ticket";
    public const string Document = "document";
    public const string Audio = "audio";

    private const long TextCap = 8L * 1024 * 1024;
    private const long AudioCap = 64L * 1024 * 1024;

    /// <summary>Registry order is display order and the order settings are normalised to.</summary>
    public static IReadOnlyList<ArtefactKind> All { get; } = new ArtefactKind[]
    {
        new(Code, "Source code",
            "Application source code. Parsed into routines; always part of a project.",
            SourceLanguageDetector.AllowedExtensions.Distinct().OrderBy(e => e, StringComparer.Ordinal).ToArray(),
            TrustTier.Structured, TextCap, true, Core: true),
        new(Sql, "Database scripts",
            "Tables, views and stored procedures (T-SQL).",
            new[] { ".sql", ".ddl" }, TrustTier.Structured, TextCap, true),
        new(Job, "Job schedules",
            "Job definitions and job plans exported from a scheduler, as XML.",
            new[] { ".xml" }, TrustTier.Structured, TextCap, true),
        new(Etl, "ETL packages",
            "Integration packages (.dtsx): sources, destinations and file paths.",
            new[] { ".dtsx" }, TrustTier.Structured, TextCap, true),
        new(Config, "Configuration",
            "Endpoints, queues, timeouts and paths per environment.",
            new[] { ".json" }, TrustTier.Structured, TextCap, true),
        new(Transcript, "Transcripts",
            "Knowledge-transfer or interview transcripts with speakers and timestamps.",
            new[] { ".vtt", ".txt" }, TrustTier.Narrative, TextCap, true),
        new(Ticket, "Tickets",
            "Incident and request exports (CSV).",
            new[] { ".csv" }, TrustTier.Narrative, TextCap, true),
        new(Document, "Documents",
            "Existing documents and notes (.docx, .md) and generic XML exports.",
            new[] { ".docx", ".md", ".xml" }, TrustTier.Narrative, TextCap, false),
        new(Audio, "Audio",
            "Recordings, attached to a transcript so a citation can play the moment.",
            new[] { ".mp3", ".wav", ".m4a" }, TrustTier.Narrative, AudioCap, false),
    };

    public static ArtefactKind? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>Kinds a project can switch on or off (everything except the core ones).</summary>
    public static IEnumerable<ArtefactKind> Optional => All.Where(k => !k.Core);

    /// <summary>Position in the registry, used to keep normalised settings in a stable order.</summary>
    public static int OrderOf(string id)
    {
        for (var i = 0; i < All.Count; i++)
            if (string.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }
}
