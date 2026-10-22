namespace Astra.Api.Persistence.Entities;

public sealed class SourceFile
{
    public Guid Id { get; set; }
    public Guid SourceVersionId { get; set; }
    public string RelativePath { get; set; } = "";
    public string FileHash { get; set; } = "";
    public int LineCount { get; set; }
    public string BlobUri { get; set; } = "";

    /// <summary>Artefact kind id (see <c>Artefacts/ArtefactKinds.cs</c>); "code" for source files.</summary>
    public string Kind { get; set; } = "code";
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }

    /// <summary>Kind-specific metadata, e.g. <c>{"attachesTo":"kt/session1.vtt"}</c> for audio.</summary>
    public string? MetaJson { get; set; }

    public SourceVersion? SourceVersion { get; set; }
    public List<Subroutine> Subroutines { get; set; } = new();
}
