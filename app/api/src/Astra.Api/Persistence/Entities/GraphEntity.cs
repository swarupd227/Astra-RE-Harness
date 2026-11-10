namespace Astra.Api.Persistence.Entities;

/// <summary>
/// What one artefact file states about the system (tables, jobs …), as a serialised <c>FactSet</c>.
/// Stored per file so the entity graph can be rebuilt from it; a file of a kind that produces facts
/// always has a row, even an empty one. Goes with its file and version.
/// </summary>
public sealed class FileFactsRow
{
    public Guid SourceFileId { get; set; }
    public Guid SourceVersionId { get; set; }
    public string FactsJson { get; set; } = "{}";
    public DateTimeOffset BuiltAt { get; set; }

    public SourceFile? SourceFile { get; set; }
}

/// <summary>
/// A thing the project's evidence says exists — a table, a stored procedure, a batch job, a package —
/// identified within a version by its type and name. <see cref="Declared"/> is false for something only
/// another thing refers to (a job that runs a procedure no attached file defines).
/// </summary>
public sealed class GraphEntity
{
    public Guid Id { get; set; }
    public Guid SourceVersionId { get; set; }
    public string Type { get; set; } = "";

    /// <summary>The name lower-cased, brackets removed: how two files' mentions of one thing are matched.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Declared { get; set; }

    public string? AttrsJson { get; set; }

    /// <summary>The short ids of the evidence chunks that show this thing (a JSON array).</summary>
    public string? EvidenceJson { get; set; }

    /// <summary>The files that declare or mention it (a JSON array of paths).</summary>
    public string? PathsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A relation between two entities of one version: reads, writes, calls, runs, precedes …</summary>
public sealed class GraphLink
{
    public Guid Id { get; set; }
    public Guid SourceVersionId { get; set; }
    public string Relation { get; set; } = "";
    public Guid FromEntityId { get; set; }
    public Guid ToEntityId { get; set; }

    /// <summary>"high" for what a rule read straight from the evidence, "medium" for a pattern match or a model's proposal.</summary>
    public string Confidence { get; set; } = "high";

    /// <summary>"rule" (read deterministically) or "model" (proposed, needs review).</summary>
    public string Origin { get; set; } = "rule";
    public string? AttrsJson { get; set; }
    public string? EvidenceJson { get; set; }

    public GraphEntity? From { get; set; }
    public GraphEntity? To { get; set; }
}
