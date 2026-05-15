namespace Astra.Api.Persistence.Entities;

public sealed class SourceVersion
{
    public Guid Id { get; set; }
    public Guid CorpusId { get; set; }
    public string? GitCommitHash { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
    public Guid? IngestedBy { get; set; }
    public string FileManifestBlobUri { get; set; } = "";

    public Corpus? Corpus { get; set; }
    public List<SourceFile> Files { get; set; } = new();
}
