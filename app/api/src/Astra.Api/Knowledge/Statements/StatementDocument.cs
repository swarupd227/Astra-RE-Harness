namespace Astra.Api.Knowledge;

/// <summary>
/// The shape of a deliverable written as statements (<c>statements/v1</c>): short claims, each tied to the
/// slot it fills and to the evidence that shows it, so every sentence can be checked, reviewed and scored on
/// its own. Markdown is derived from this (for export); it is never the source.
/// </summary>
public static class StatementSchema
{
    public const string Id = "statements/v1";
}

/// <summary>How far a statement can be trusted, as decided by the grounding gate (never by the writer).</summary>
public static class StatementStatus
{
    /// <summary>Shown by structured evidence (code, SQL, a scheduler export, configuration) and checked to say what the statement says, or confirmed by the claims pipeline.</summary>
    public const string Supported = "supported";

    /// <summary>Only people said or wrote it (a transcript, a ticket, an old document), or the check found it only partly said.</summary>
    public const string Unverified = "unverified";

    /// <summary>Two pieces of evidence disagree about it, and both are named.</summary>
    public const string Conflict = "conflict";

    public static bool IsKnown(string? s) => s is Supported or Unverified or Conflict;
}

/// <summary>The support check's verdict on whether the cited evidence says what the statement says.</summary>
public static class SupportVerdict
{
    public const string Yes = "yes";
    public const string Partly = "partly";
    public const string No = "no";

    public static bool IsKnown(string? s) => s is Yes or Partly or No;
}

/// <param name="Id">Stable for the same slot, text and evidence, so a statement keeps its identity across regenerations.</param>
/// <param name="SlotKey">The slot of the deliverable this fills (<c>runbook:failure-handling</c>, <c>entity:job:TLR_EOD_POST</c>).</param>
/// <param name="Citations">Evidence short ids (<c>SQL-3fa9c01e</c>) that show it.</param>
/// <param name="Status">Set by the grounding gate; null until then.</param>
/// <param name="Support">The support check's verdict; null when it was not checked.</param>
/// <param name="Notes">What the gate said about it, in plain sentences.</param>
public sealed record StatementItem(
    string Id,
    string SlotKey,
    string Text,
    IReadOnlyList<string> Citations,
    string? Status = null,
    string? Support = null,
    IReadOnlyList<string>? Notes = null);

public sealed record StatementSection(string Heading, IReadOnlyList<StatementItem> Items);

/// <summary>
/// Two pieces of evidence that disagree about a statement. A conflict is only real when it names at least two
/// different pieces of evidence that exist; anything less is downgraded by the gate.
/// </summary>
public sealed record StatementConflict(string ItemId, IReadOnlyList<string> EvidenceIds, string Summary);

public sealed record OpenQuestion(string Id, string Text, string? SlotKey = null, IReadOnlyList<string>? Citations = null);

public sealed record StatementDocument(
    string Kind,
    string Title,
    IReadOnlyList<StatementSection> Sections,
    IReadOnlyList<StatementConflict> Conflicts,
    IReadOnlyList<OpenQuestion> OpenQuestions,
    double? Confidence = null)
{
    public string Schema => StatementSchema.Id;

    public IEnumerable<StatementItem> Items => Sections.SelectMany(s => s.Items);

    public static StatementDocument Empty(string kind, string title) =>
        new(kind, title, Array.Empty<StatementSection>(), Array.Empty<StatementConflict>(), Array.Empty<OpenQuestion>());
}
