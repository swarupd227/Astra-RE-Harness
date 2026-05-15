namespace Astra.Api.Persistence.Entities;

public sealed class SourceFile
{
    public Guid Id { get; set; }
    public Guid SourceVersionId { get; set; }
    public string RelativePath { get; set; } = "";
    public string FileHash { get; set; } = "";
    public int LineCount { get; set; }
    public string BlobUri { get; set; } = "";

    public SourceVersion? SourceVersion { get; set; }
    public List<Subroutine> Subroutines { get; set; } = new();
}
