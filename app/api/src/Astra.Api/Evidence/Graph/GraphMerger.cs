using System.Text.Json;

namespace Astra.Api.Evidence;

/// <summary>One file's facts, with the short id of each chunk they point at (locator → id).</summary>
public sealed record FileFactSet(
    string Path,
    FactSet Facts,
    IReadOnlyDictionary<string, string> ShortIdByLocator);

public sealed class MergedEntity
{
    public string Type { get; init; } = "";
    public string Key { get; init; } = "";
    public string Name { get; set; } = "";

    /// <summary>False when the entity is only named by a link (a job runs a procedure no attached file defines).</summary>
    public bool Declared { get; set; }

    public Dictionary<string, JsonElement> Attrs { get; } = new(StringComparer.Ordinal);
    public List<string> EvidenceIds { get; } = new();
    public List<string> Paths { get; } = new();
}

public sealed class MergedLink
{
    public string Relation { get; init; } = "";
    public MergedEntity From { get; init; } = null!;
    public MergedEntity To { get; init; } = null!;
    public string Confidence { get; set; } = EntityVocabulary.High;
    public string Origin { get; init; } = "rule";
    public Dictionary<string, JsonElement> Attrs { get; } = new(StringComparer.Ordinal);
    public List<string> EvidenceIds { get; } = new();
}

public sealed record MergedGraph(IReadOnlyList<MergedEntity> Entities, IReadOnlyList<MergedLink> Links);

/// <summary>
/// Joins every file's facts into one graph. Things meet by type and name, so a job that runs
/// <c>usp_PostStage</c> and the script that defines it are the same procedure; the result does not
/// depend on the order files were attached in (files are taken in path order, and the first file to say
/// something wins a conflicting attribute). Pure: no database, no clock.
/// </summary>
public static class GraphMerger
{
    /// <summary>The identity of a name: case-insensitive, brackets and quotes around it ignored.</summary>
    public static string KeyOf(string name) => name.Trim().Trim('[', ']', '"', '`').Trim().ToLowerInvariant();

    public static MergedGraph Merge(IEnumerable<FileFactSet> files)
    {
        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var entities = new Dictionary<(string Type, string Key), MergedEntity>();

        // Pass 1: everything a file declares.
        foreach (var file in ordered)
        {
            foreach (var fact in file.Facts.Entities)
            {
                if (string.IsNullOrWhiteSpace(fact.Name)) continue;
                var entity = GetOrAdd(entities, fact.Type, fact.Name);
                entity.Declared = true;
                AddEvidence(entity.EvidenceIds, file, fact.Locator);
                if (!entity.Paths.Contains(file.Path)) entity.Paths.Add(file.Path);
                MergeAttrs(entity.Attrs, fact.Attrs);
            }
        }

        // Pass 2: the relations, once every file's entities are known — a link may name a thing declared in a later file.
        var links = new Dictionary<(string Relation, MergedEntity From, MergedEntity To), MergedLink>();
        foreach (var file in ordered)
        {
            foreach (var fact in file.Facts.Links)
            {
                if (string.IsNullOrWhiteSpace(fact.From.Name) || string.IsNullOrWhiteSpace(fact.To.Name)) continue;
                var from = Resolve(entities, fact.From);
                var to = Resolve(entities, fact.To);
                if (ReferenceEquals(from, to) && fact.Relation != EntityVocabulary.Calls) continue;

                if (!from.Declared && !from.Paths.Contains(file.Path)) from.Paths.Add(file.Path);
                if (!to.Declared && !to.Paths.Contains(file.Path)) to.Paths.Add(file.Path);
                if (!from.Declared) AddEvidence(from.EvidenceIds, file, fact.Locator);
                if (!to.Declared) AddEvidence(to.EvidenceIds, file, fact.Locator);

                var id = (fact.Relation, from, to);
                if (!links.TryGetValue(id, out var link))
                {
                    link = new MergedLink { Relation = fact.Relation, From = from, To = to, Confidence = fact.Confidence };
                    links[id] = link;
                }
                else if (EntityVocabulary.ConfidenceRank(fact.Confidence) > EntityVocabulary.ConfidenceRank(link.Confidence))
                {
                    link.Confidence = fact.Confidence;
                }
                AddEvidence(link.EvidenceIds, file, fact.Locator);
                MergeAttrs(link.Attrs, fact.Attrs);
            }
        }

        return new MergedGraph(
            entities.Values.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal).ToList(),
            links.Values
                .OrderBy(l => l.Relation, StringComparer.Ordinal)
                .ThenBy(l => l.From.Type, StringComparer.Ordinal).ThenBy(l => l.From.Key, StringComparer.Ordinal)
                .ThenBy(l => l.To.Type, StringComparer.Ordinal).ThenBy(l => l.To.Key, StringComparer.Ordinal)
                .ToList());
    }

    private static MergedEntity GetOrAdd(Dictionary<(string, string), MergedEntity> entities, string type, string name)
    {
        var key = KeyOf(name);
        if (!entities.TryGetValue((type, key), out var e))
        {
            e = new MergedEntity { Type = type, Key = key, Name = name.Trim() };
            entities[(type, key)] = e;
        }
        return e;
    }

    /// <summary>
    /// The entity a link end means. The same type and name if there is one; a reference to a table also
    /// finds a view of that name (the script that reads it cannot tell which it is), and a reference to a
    /// procedure finds a function. Otherwise the end becomes an entity nothing declares.
    /// </summary>
    private static MergedEntity Resolve(Dictionary<(string Type, string Key), MergedEntity> entities, EntityRefFact reference)
    {
        var key = KeyOf(reference.Name);
        if (entities.TryGetValue((reference.Type, key), out var exact)) return exact;

        var alternative = reference.Type switch
        {
            EntityVocabulary.Table => EntityVocabulary.View,
            EntityVocabulary.Procedure => EntityVocabulary.Function,
            _ => null,
        };
        if (alternative is not null && entities.TryGetValue((alternative, key), out var other)) return other;

        return GetOrAdd(entities, reference.Type, reference.Name);
    }

    private static void AddEvidence(List<string> ids, FileFactSet file, string? locator)
    {
        if (locator is null || !file.ShortIdByLocator.TryGetValue(locator, out var id)) return;
        if (!ids.Contains(id)) ids.Add(id);
    }

    /// <summary>
    /// A key a file says first stays; a list is joined with what was already there, in order. Nothing is
    /// overwritten, so the result is the same whichever order the facts arrive in once files are sorted.
    /// </summary>
    private static void MergeAttrs(Dictionary<string, JsonElement> into, IReadOnlyDictionary<string, object?>? incoming)
    {
        if (incoming is null) return;
        foreach (var (key, raw) in incoming)
        {
            if (raw is null) continue;
            var value = JsonSerializer.SerializeToElement(raw);
            if (value.ValueKind == JsonValueKind.Null) continue;

            if (!into.TryGetValue(key, out var existing))
            {
                into[key] = value;
            }
            else if (existing.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.Array)
            {
                var have = existing.EnumerateArray().Select(e => e.GetRawText()).ToHashSet(StringComparer.Ordinal);
                var joined = existing.EnumerateArray().Concat(value.EnumerateArray().Where(e => !have.Contains(e.GetRawText()))).ToList();
                into[key] = JsonSerializer.SerializeToElement(joined);
            }
        }
    }
}
