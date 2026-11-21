namespace Astra.Api.Knowledge;

/// <summary>One fixed slot of a deliverable: a thing the finished document must say something about.</summary>
/// <param name="Key">Stable and shaped <c>area:name</c> (<c>runbook:failure-handling</c>); every statement written for it carries it.</param>
/// <param name="Brief">What a statement for this slot is about, in a sentence a writer can follow.</param>
public sealed record SlotDefinition(string Key, string Label, string? Brief = null);

/// <summary>
/// Slots that exist once per thing the project turned out to have (a batch job, an interface, a known error): their
/// keys are <see cref="KeyPrefix"/> plus the thing's name (<c>entity:job:TLR_EOD_POST</c>). <see cref="CountKey"/> names the
/// entry of the project's expected counts that says how many there should be.
/// </summary>
public sealed record EntitySlots(string KeyPrefix, string CountKey, string Label, string? Brief = null);

/// <param name="Weight">Its share of the whole score; shares are relative, they do not have to add up to 100.</param>
/// <param name="Generator">Which writer produces it (resolved by the capture run).</param>
public sealed record DeliverableDefinition(
    string Id,
    string Label,
    double Weight,
    string Generator,
    IReadOnlyList<SlotDefinition> StaticSlots,
    EntitySlots? EntitySlots)
{
    /// <summary>Documents of a deliverable are stored under this section kind, never under a legacy kind.</summary>
    public string SectionKind => DeliverableKinds.SectionKindOf(Id);
}

/// <summary>What a slot earns for what has been approved for it.</summary>
/// <param name="ApprovedAllSupported">Approved, and every statement in it supported.</param>
/// <param name="ApprovedWithUnverified">Approved, but at least one statement is only unverified.</param>
/// <param name="Otherwise">Not approved, rejected, or approved with a conflict still open.</param>
public sealed record CreditRules(double ApprovedAllSupported, double ApprovedWithUnverified, double Otherwise);

/// <summary>Where knowledge dependency (100 minus completeness) turns from low to medium to high.</summary>
public sealed record DependencyBands(double HighAbove, double MediumFrom)
{
    public string BandOf(double dependencyPercent) =>
        dependencyPercent > HighAbove ? "high" : dependencyPercent >= MediumFrom ? "medium" : "low";
}

/// <summary>The dependency a portfolio is to be under by each date, in percent.</summary>
public sealed record DependencyTargets(double Day90, double Day180, double Day365);

/// <summary>
/// A pack is data: the deliverables a kind of programme produces, how much each weighs, which slots each must fill,
/// how approvals earn credit and how the score is banded. Nothing in the platform is named for a client or an
/// application; a pack says what to produce and how it is scored, and a project chooses one in its settings.
/// </summary>
public sealed record Pack(
    string Id,
    string Title,
    int Version,
    IReadOnlyDictionary<string, string> Vocabulary,
    CreditRules Credit,
    DependencyBands Bands,
    DependencyTargets Targets,
    IReadOnlyDictionary<string, double> TierWeights,
    IReadOnlyList<DeliverableDefinition> Deliverables)
{
    public DeliverableDefinition? Deliverable(string id) =>
        Deliverables.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    /// <summary>The word to show for a term the pack renames ("knowledgeHolder" → "contractor"), or the term as it is.</summary>
    public string Term(string key, string fallback) => Vocabulary.TryGetValue(key, out var v) ? v : fallback;
}

public static class DeliverableKinds
{
    /// <summary>The scope of a deliverable's sections; the documentation routes for the older kinds leave it alone.</summary>
    public const string Scope = "deliverable";

    public const string SectionKindPrefix = "d-";

    public static string SectionKindOf(string deliverableId) => SectionKindPrefix + deliverableId;

    public static bool IsDeliverableSectionKind(string? sectionKind) =>
        sectionKind is not null && sectionKind.StartsWith(SectionKindPrefix, StringComparison.Ordinal) && sectionKind.Length > SectionKindPrefix.Length;

    public static string DeliverableIdOf(string sectionKind) => sectionKind[SectionKindPrefix.Length..];
}
