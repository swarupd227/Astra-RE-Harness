namespace Astra.Api.Persistence.Entities;

/// <summary>
/// One citable piece of one file in one source version: a routine's lines, a window of a SQL
/// script, a ticket, a turn of a transcript. Statements about a project cite these by
/// <see cref="ShortId"/>. Rows belong to a version and go with it; a re-sync copies the
/// artefact chunks it carries forward (same short id) and rebuilds the code ones.
/// </summary>
public sealed class EvidenceChunk
{
    public Guid Id { get; set; }

    /// <summary>Unique within a version, e.g. <c>SQL-3fa9c01e</c> (see <c>Evidence/EvidenceIds.cs</c>).</summary>
    public string ShortId { get; set; } = "";
    public Guid SourceVersionId { get; set; }
    public Guid SourceFileId { get; set; }

    /// <summary>The file's artefact kind id.</summary>
    public string Kind { get; set; } = "code";

    /// <summary>"structured" (parser-derived) or "narrative" (what people said or wrote).</summary>
    public string TrustTier { get; set; } = "structured";

    /// <summary>Where in the file: <c>L120-168</c>, <c>row:INC-1</c>, <c>t=00:14:32-00:15:40</c> …</summary>
    public string Locator { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>The chunk's own text. Null for source code: its text is the file's lines.</summary>
    public string? Text { get; set; }

    /// <summary>Kind-specific facts about the chunk (JSON), e.g. the routine behind a code chunk.</summary>
    public string? StructuredJson { get; set; }

    /// <summary>Changes when the cited content changes (a hash of the text, or of file + span for code).</summary>
    public string TextHash { get; set; } = "";
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public SourceFile? SourceFile { get; set; }
}
