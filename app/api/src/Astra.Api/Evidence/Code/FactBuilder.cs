using System.Text.Json;

namespace Astra.Api.Evidence;

/// <summary>
/// Collects the entities and links one file states, each once: a class that calls the same endpoint from
/// three routines is one link with three sites, not three links. What differs between the sightings
/// (which routine, which line, whether it was inside a loop) is kept as <c>sites</c>, because the graph
/// keeps a list when two files say the same thing and a single value when one does.
/// </summary>
internal sealed class FactBuilder
{
    public const int MaxSitesPerLink = 20;

    private sealed class EntityAcc
    {
        public string Type = "";
        public string Name = "";
        public string? Locator;
        public Dictionary<string, object?> Attrs = new(StringComparer.Ordinal);
    }

    private sealed class LinkAcc
    {
        public string Relation = "";
        public (string Type, string Name) From;
        public (string Type, string Name) To;
        public string? Locator;
        public string Confidence = EntityVocabulary.High;
        public Dictionary<string, object?> Attrs = new(StringComparer.Ordinal);
        public List<Dictionary<string, object?>> Sites = new();
        public HashSet<string> SiteKeys = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<(string, string), EntityAcc> _entities = new();
    private readonly Dictionary<(string, string, string, string, string), LinkAcc> _links = new();
    private readonly List<(string, string)> _entityOrder = new();
    private readonly List<(string, string, string, string, string)> _linkOrder = new();

    public int LinkCount => _links.Count;

    /// <summary>True when something has been linked from this thing already.</summary>
    public bool HasLinkFrom(string type, string name) =>
        _links.Keys.Any(k => k.Item2 == type && k.Item3 == GraphMerger.KeyOf(name));

    public void Entity(string type, string name, string? locator, IReadOnlyDictionary<string, object?>? attrs = null)
    {
        var key = (type, GraphMerger.KeyOf(name));
        if (!_entities.TryGetValue(key, out var e))
        {
            e = new EntityAcc { Type = type, Name = name.Trim(), Locator = locator };
            _entities[key] = e;
            _entityOrder.Add(key);
        }
        e.Locator ??= locator;
        if (attrs is null) return;
        foreach (var (k, v) in attrs)
            if (v is not null && !e.Attrs.ContainsKey(k)) e.Attrs[k] = v;
    }

    public void Link(
        string relation,
        (string Type, string Name) from,
        (string Type, string Name) to,
        string locator,
        string confidence,
        IReadOnlyDictionary<string, object?>? site = null,
        IReadOnlyDictionary<string, object?>? attrs = null)
    {
        var key = (relation, from.Type, GraphMerger.KeyOf(from.Name), to.Type, GraphMerger.KeyOf(to.Name));
        if (!_links.TryGetValue(key, out var l))
        {
            l = new LinkAcc { Relation = relation, From = from, To = to, Locator = locator, Confidence = confidence };
            _links[key] = l;
            _linkOrder.Add(key);
        }
        else if (EntityVocabulary.ConfidenceRank(confidence) > EntityVocabulary.ConfidenceRank(l.Confidence))
        {
            l.Confidence = confidence;
        }

        if (attrs is not null)
            foreach (var (k, v) in attrs)
                if (v is not null && !l.Attrs.ContainsKey(k)) l.Attrs[k] = v;

        if (site is { Count: > 0 } && l.Sites.Count < MaxSitesPerLink)
        {
            var clean = site.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (l.SiteKeys.Add(JsonSerializer.Serialize(clean.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())))
                l.Sites.Add(clean);
        }
    }

    public FactSet Build()
    {
        var entities = _entityOrder.Select(k =>
        {
            var e = _entities[k];
            return new EntityFact(e.Type, e.Name, e.Locator, e.Attrs.Count == 0 ? null : e.Attrs);
        }).ToList();

        var links = _linkOrder.Select(k =>
        {
            var l = _links[k];
            var attrs = new Dictionary<string, object?>(l.Attrs, StringComparer.Ordinal);
            if (l.Sites.Count > 0) attrs["sites"] = l.Sites;
            return new LinkFact(
                l.Relation,
                new EntityRefFact(l.From.Type, l.From.Name),
                new EntityRefFact(l.To.Type, l.To.Name),
                l.Locator,
                l.Confidence,
                attrs.Count == 0 ? null : attrs);
        }).ToList();
        return new FactSet(entities, links);
    }
}
