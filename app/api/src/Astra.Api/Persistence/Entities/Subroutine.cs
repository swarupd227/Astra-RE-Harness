using System.Text.Json;

namespace Astra.Api.Persistence.Entities;

public sealed class Subroutine
{
    public Guid Id { get; set; }
    public Guid SourceFileId { get; set; }
    public string Name { get; set; } = "";
    public string Signature { get; set; } = "";
    public int LineStart { get; set; }
    public int LineEnd { get; set; }

    /// <summary>JSON document. List of COMMON block names referenced.</summary>
    public JsonDocument? CommonBlockRefs { get; set; }
    /// <summary>JSON document. List of subroutines this routine calls.</summary>
    public JsonDocument? CalledSubroutines { get; set; }
    /// <summary>JSON document. Detected ISAM/file I/O patterns.</summary>
    public JsonDocument? IoPatterns { get; set; }

    public string? ParsedAstBlobUri { get; set; }
    public string State { get; set; } = "PARSED";

    /// <summary>
    /// Source-language id the parser identified — matches the
    /// spec-schema id (e.g. "fortran-f77", "cobol"). Drives prompt +
    /// archetype routing in the extraction and scaffold pipelines.
    /// Default keeps rows from pre-Phase-5.2 deployments valid.
    /// </summary>
    public string SourceLanguage { get; set; } = "fortran-f77";

    public SourceFile? SourceFile { get; set; }
}
