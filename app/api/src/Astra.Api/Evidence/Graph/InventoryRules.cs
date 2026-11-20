namespace Astra.Api.Evidence;

/// <summary>An entity as the inventory counts it. <see cref="Ref"/> is <c>type:Name</c>.</summary>
public sealed record InventoryEntity(string Ref, string Type, string Name, bool Declared, IReadOnlyList<string> EvidenceIds);

public sealed record InventoryLink(string Relation, string From, string To);

/// <param name="Found">How many were found; null for a count the project expects that the platform has no way of counting.</param>
/// <param name="Expected">What the project says there should be (from its settings), if it says.</param>
public sealed record InventoryMeasure(string Key, string Label, int? Found, int? Expected);

public sealed record InventoryFindingItem(string Ref, string Name, IReadOnlyList<string> EvidenceIds);

public sealed record InventoryFinding(string Code, string Title, string Explanation, IReadOnlyList<InventoryFindingItem> Items);

/// <summary>
/// The counts of what a project's evidence says exists, and the gaps that follow from how the pieces
/// join up. Pure functions of the entity graph: nothing here asks a model, and every count can be checked
/// by listing the entities of that type.
/// </summary>
public static class InventoryRules
{
    private static readonly (string Key, string Label, string Type)[] ByType =
    {
        ("tables", "Tables", EntityVocabulary.Table),
        ("views", "Views", EntityVocabulary.View),
        ("procedures", "Stored procedures", EntityVocabulary.Procedure),
        ("functions", "Functions", EntityVocabulary.Function),
        ("jobs", "Batch jobs and plans", EntityVocabulary.Job),
        ("packages", "Integration packages", EntityVocabulary.Package),
        ("config-keys", "Configuration keys", EntityVocabulary.ConfigKey),
        ("classes", "Classes that use something outside themselves", EntityVocabulary.Class),
        ("endpoints", "Web endpoints called", EntityVocabulary.Endpoint),
        ("queues", "Message queues and exchanges", EntityVocabulary.Queue),
    };

    public const string RoutinesKey = "routines";
    public const string InterfacesKey = "interfaces";

    /// <summary>The kinds of thing whose absence from the evidence is a gap when something refers to them.</summary>
    private static readonly HashSet<string> Definable = new(StringComparer.Ordinal)
    {
        EntityVocabulary.Table, EntityVocabulary.View, EntityVocabulary.Procedure, EntityVocabulary.Function,
        EntityVocabulary.Job, EntityVocabulary.Package,
    };

    private static readonly HashSet<string> Exchanging = new(StringComparer.Ordinal)
    {
        EntityVocabulary.Reads, EntityVocabulary.Writes, EntityVocabulary.Triggers,
    };

    private static readonly HashSet<string> Actors = new(StringComparer.Ordinal)
    {
        EntityVocabulary.Job, EntityVocabulary.Package, EntityVocabulary.Class, EntityVocabulary.Script,
    };

    /// <param name="routines">How many routines the parser found in the code (not an entity of the graph).</param>
    /// <param name="expected">The counts the project's settings expect, by measure key.</param>
    public static IReadOnlyList<InventoryMeasure> Measures(
        IReadOnlyList<InventoryEntity> entities,
        IReadOnlyList<InventoryLink> links,
        int routines,
        IReadOnlyDictionary<string, int> expected)
    {
        var measures = new List<InventoryMeasure>();
        foreach (var (key, label, type) in ByType)
            measures.Add(new InventoryMeasure(key, label, entities.Count(e => e.Type == type && e.Declared), expected.TryGetValue(key, out var x) ? x : null));

        measures.Add(new InventoryMeasure(RoutinesKey, "Routines in the code", routines, expected.TryGetValue(RoutinesKey, out var r) ? r : null));
        measures.Add(new InventoryMeasure(
            InterfacesKey,
            "Interface candidates: web endpoints, queues and files a job, package or class exchanges",
            InterfaceCandidates(entities, links).Count,
            expected.TryGetValue(InterfacesKey, out var i) ? i : null));

        // A count the project expects that is not one of these has no way of being found here: say so, do not drop it.
        var known = measures.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, count) in expected.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!known.Contains(key)) measures.Add(new InventoryMeasure(key, key, null, count));
        return measures;
    }

    /// <summary>
    /// Things that cross the boundary of the application as far as the evidence shows: a web endpoint, a queue,
    /// and a file that a job, a package or a class reads, writes or is triggered by. An upper bound, not the
    /// interface map: a person (or the interface writer) decides which of them are interfaces worth documenting.
    /// </summary>
    public static IReadOnlyList<InventoryEntity> InterfaceCandidates(IReadOnlyList<InventoryEntity> entities, IReadOnlyList<InventoryLink> links)
    {
        var byRef = entities.ToDictionary(e => e.Ref, StringComparer.Ordinal);
        var exchanged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in links.Where(l => Exchanging.Contains(l.Relation)))
        {
            if (!byRef.TryGetValue(l.From, out var from) || !byRef.TryGetValue(l.To, out var to)) continue;
            if (to.Type == EntityVocabulary.File && Actors.Contains(from.Type)) exchanged.Add(to.Ref);
            if (from.Type == EntityVocabulary.File && Actors.Contains(to.Type)) exchanged.Add(from.Ref);
        }
        return entities
            .Where(e => e.Type is EntityVocabulary.Endpoint or EntityVocabulary.Queue || (e.Type == EntityVocabulary.File && exchanged.Contains(e.Ref)))
            .OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<InventoryFinding> Findings(IReadOnlyList<InventoryEntity> entities, IReadOnlyList<InventoryLink> links)
    {
        var findings = new List<InventoryFinding>();

        void Add(string code, string title, string explanation, IEnumerable<InventoryEntity> items)
        {
            var list = items.OrderBy(e => e.Ref, StringComparer.Ordinal)
                .Select(e => new InventoryFindingItem(e.Ref, e.Name, e.EvidenceIds))
                .ToList();
            if (list.Count > 0) findings.Add(new InventoryFinding(code, title, explanation, list));
        }

        Add("referenced-not-defined",
            "Named by something, defined by nothing attached",
            "A job, procedure, table or package is run, read or written, but no attached file defines it. Attach the export or script, or ask who owns it.",
            entities.Where(e => !e.Declared && Definable.Contains(e.Type)));

        var readBy = links.Where(l => l.Relation == EntityVocabulary.Reads).Select(l => l.To).ToHashSet(StringComparer.Ordinal);
        Add("setting-not-configured",
            "Read by the code, set by no attached configuration",
            "The code reads this setting but none of the attached configuration files has it. Attach the configuration for each environment.",
            entities.Where(e => e.Type == EntityVocabulary.ConfigKey && !e.Declared && readBy.Contains(e.Ref)));

        // "Never read" only means something once there is code (or a package) that could have read it.
        var somethingReads = entities.Any(e => e.Type is EntityVocabulary.Class or EntityVocabulary.Package);
        if (somethingReads)
        {
            var configures = links.Where(l => l.Relation == EntityVocabulary.Configures).Select(l => l.From).ToHashSet(StringComparer.Ordinal);
            Add("setting-never-read",
                "Set in configuration, read by nothing attached",
                "A configuration setting that no attached code or package reads. It may be dead, or the code that reads it may not be attached; a documented workaround that changes it would not work.",
                entities.Where(e => e.Type == EntityVocabulary.ConfigKey && e.Declared && !readBy.Contains(e.Ref) && !configures.Contains(e.Ref)));
        }
        return findings;
    }
}
