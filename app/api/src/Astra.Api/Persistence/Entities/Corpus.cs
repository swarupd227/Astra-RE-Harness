namespace Astra.Api.Persistence.Entities;

public sealed class Corpus
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string SourceType { get; set; } = "git";  // git | upload
    public string? SourceUrl { get; set; }
    public string? Branch { get; set; }
    public string? SourceRoot { get; set; }
    public string State { get; set; } = "INGESTED";
    public int FileCount { get; set; }
    public int TotalLoc { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? LatestVersionId { get; set; }

    /// <summary>
    /// Per-project settings (see <c>Artefacts/ProjectSettings.cs</c>): which optional
    /// artefact kinds the project takes, display banner, programme metadata. Null means
    /// "all defaults" — rows that predate the column.
    /// </summary>
    public string? SettingsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<SourceVersion> Versions { get; set; } = new();
}
