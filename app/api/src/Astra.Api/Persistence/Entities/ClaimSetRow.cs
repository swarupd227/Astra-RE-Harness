namespace Astra.Api.Persistence.Entities;

/// <summary>
/// The claims read from a source version's narrative evidence, what the structured evidence says about each, and the
/// ticket clusters, as one document (<c>claims/v1</c>). Derived and replaceable: a run writes it whole and a new
/// version of the project starts without one.
/// </summary>
public sealed class ClaimSetRow
{
    public Guid SourceVersionId { get; set; }
    public string Json { get; set; } = "{}";
    public int ModelCalls { get; set; }
    public int ElapsedMs { get; set; }
    public DateTimeOffset BuiltAt { get; set; }
}
