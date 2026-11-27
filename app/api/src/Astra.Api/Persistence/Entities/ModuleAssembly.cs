namespace Astra.Api.Persistence.Entities;

/// <summary>
/// One assembled application: the checked packages of a module's code units merged into a single project
/// (<see cref="Astra.Api.Assembly.IModuleAssembler"/>), and how its build and tests went. The project itself is a file
/// manifest in blob storage, like a scaffold's.
/// </summary>
public sealed class ModuleAssembly
{
    public Guid Id { get; set; }
    public Guid CorpusId { get; set; }
    public Guid? SourceVersionId { get; set; }
    public string Name { get; set; } = "";
    public string TargetStack { get; set; } = "java-spring";

    /// <summary>RUNNING while it is assembled and checked; then PASSED (builds, tests pass), FAILED, or ERRORED (could not run).</summary>
    public string State { get; set; } = "RUNNING";

    /// <summary>What was asked for: {pathPrefix, waveId, unitIds, requireTests}.</summary>
    public string SelectionJson { get; set; } = "{}";

    /// <summary>The assembler's report plus the units left out and why.</summary>
    public string? ReportJson { get; set; }

    public string? PackageBlobUri { get; set; }
    public string? GateLogBlobUri { get; set; }
    public int UnitCount { get; set; }
    public int ExcludedCount { get; set; }
    public int FileCount { get; set; }

    /// <summary>PASSED / FAILED / ERRORED / null when not run.</summary>
    public string? CompileStatus { get; set; }
    public string? TestStatus { get; set; }
    public int TestsTotal { get; set; }
    public int TestsFailed { get; set; }
    public string Summary { get; set; } = "";
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
