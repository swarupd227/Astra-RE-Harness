using System.Text.Json;

namespace Astra.Api.Persistence.Entities;

public sealed class Spec
{
    public Guid Id { get; set; }
    public Guid SubroutineId { get; set; }
    public Guid SourceVersionId { get; set; }
    public string State { get; set; } = "DRAFT";

    /// <summary>
    /// Canonical spec JSON conforming to spec/v1.json (data-model.md §4).
    /// </summary>
    public JsonDocument SpecJson { get; set; } = JsonDocument.Parse("{}");

    public Guid? LlmCallId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When this spec was carried forward from a previous source version
    /// during re-sync (hash unchanged), points at the prior spec row.
    /// Null for freshly extracted specs. Drives the audit trail when an
    /// auditor asks "what did this spec descend from?".
    /// </summary>
    public Guid? PreviousSpecId { get; set; }

    public Subroutine? Subroutine { get; set; }
    public LlmCall? LlmCall { get; set; }
}
