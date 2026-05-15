namespace Astra.Api.Persistence.Entities;

public sealed class Scaffold
{
    public Guid Id { get; set; }
    public Guid SpecId { get; set; }

    /// <summary>"SCAFFOLDING" | "SCAFFOLDED" | "COMMITTED" | "FAILED".</summary>
    public string State { get; set; } = "SCAFFOLDING";

    public Guid? LlmCallId { get; set; }

    /// <summary>Target platform identifier — e.g. "dotnet8".</summary>
    public string TargetPlatform { get; set; } = "dotnet8";

    /// <summary>
    /// MinIO blob URI of the manifest JSON that lists every generated file
    /// (path, language, content, derivedFrom claim ids, todo count).
    /// </summary>
    public string PackageBlobUri { get; set; } = "";

    /// <summary>Total file count in the package — denormalised for cheap listing.</summary>
    public int FileCount { get; set; }
    public int TotalLines { get; set; }
    public int TodoCount { get; set; }

    public string? GitBranch { get; set; }
    public string? GitCommitHash { get; set; }
    public string? GitCommitUrl { get; set; }

    public Guid? GeneratedBy { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }

    public Spec? Spec { get; set; }
    public LlmCall? LlmCall { get; set; }
}
