using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>A piece of what people said or wrote (a ticket row, a turn of a transcript, a section of a document), as the linking rules read it.</summary>
public sealed record NarrativeChunk(string ShortId, string Kind, string Text);

/// <summary>
/// Rules that join what separate files say, applied to the merged graph. They are deterministic: the same
/// facts and the same text always give the same links, and every link or attribute they add says which
/// rule made it (<c>rule</c>) and how sure it is. A model proposes nothing here.
/// <list type="bullet">
/// <item><b>Setting ↔ address ↔ endpoint.</b> An endpoint whose base address is read from a setting gets that
/// setting's value in every environment (<c>resolved</c>); one that has a literal base address in code is
/// linked to a setting that holds the same address (<c>config-url</c>); a queue named by a setting is linked
/// to it (<c>config-name</c>).</item>
/// <item><b>Mentions.</b> A ticket, a turn of a transcript or a section of a document that names a thing —
/// by its name, by a constraint it owns, or (for a ticket) by its <c>cmdb_ci</c> — is listed on that thing as
/// narrative evidence (<c>mentions</c>), apart from the structured evidence that declares it, because what
/// people said has to be checked against what the files show.</item>
/// </list>
/// </summary>
public static class LinkingRules
{
    public const string RuleConfigUrl = "config-url";
    public const string RuleConfigName = "config-name";
    public const int MaxMentionsPerKind = 25;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex Origin = new(@"^(?<o>https?://[^/?#{}\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Token = new(@"[A-Za-z0-9_](?:[A-Za-z0-9_.:\-]*[A-Za-z0-9_])?", RegexOptions.Compiled);
    private static readonly Regex ConfigurationItem = new(
        @"^\s*(?:cmdb[_ ]?ci|configuration[_ ]item)\s*:\s*(?<v>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly HashSet<string> NotMentioned = new(StringComparer.Ordinal)
    {
        EntityVocabulary.File, EntityVocabulary.Script, EntityVocabulary.Endpoint,
    };

    public static MergedGraph Apply(MergedGraph graph, IReadOnlyList<NarrativeChunk> narrative)
    {
        var links = graph.Links.ToList();
        ResolveSettings(graph, links);
        Mention(graph, narrative);
        return new MergedGraph(
            graph.Entities,
            links.OrderBy(l => l.Relation, StringComparer.Ordinal)
                .ThenBy(l => l.From.Type, StringComparer.Ordinal).ThenBy(l => l.From.Key, StringComparer.Ordinal)
                .ThenBy(l => l.To.Type, StringComparer.Ordinal).ThenBy(l => l.To.Key, StringComparer.Ordinal)
                .ToList());
    }

    // ── setting ↔ address ↔ endpoint ────────────────────────────────────

    private sealed record SettingValue(string File, string Value);

    private static void ResolveSettings(MergedGraph graph, List<MergedLink> links)
    {
        var settings = graph.Entities.Where(e => e.Type == EntityVocabulary.ConfigKey).ToDictionary(e => e.Key);
        if (settings.Count == 0) return;

        var existing = new HashSet<(string, MergedEntity, MergedEntity)>(links.Select(l => (l.Relation, l.From, l.To)));
        var valuesOf = settings.Values.ToDictionary(s => s, ValuesOf);

        void Configure(MergedEntity setting, MergedEntity target, string rule, string role, string confidence)
        {
            if (!existing.Add((EntityVocabulary.Configures, setting, target))) return;
            var link = new MergedLink
            {
                Relation = EntityVocabulary.Configures,
                From = setting,
                To = target,
                Confidence = confidence,
                Origin = "rule",
            };
            link.Attrs["rule"] = JsonSerializer.SerializeToElement(rule);
            link.Attrs["role"] = JsonSerializer.SerializeToElement(role);
            foreach (var id in setting.EvidenceIds.Concat(target.EvidenceIds).Distinct()) link.EvidenceIds.Add(id);
            links.Add(link);
        }

        foreach (var e in graph.Entities)
        {
            if (e.Type == EntityVocabulary.Endpoint)
            {
                var baseKey = StringAttr(e, "baseKey");
                if (baseKey is not null && settings.TryGetValue(GraphMerger.KeyOf(baseKey), out var baseSetting))
                {
                    SetResolved(e, valuesOf[baseSetting]);
                }
                else if (baseKey is null && StringAttr(e, "baseUrl") is { } literal)
                {
                    // The address is written in the code; a setting that holds the same address is the way to change it.
                    foreach (var s in settings.Values)
                        if (valuesOf[s].Any(v => OriginOf(v.Value) is { } o && o.Equals(literal, StringComparison.OrdinalIgnoreCase)))
                            Configure(s, e, RuleConfigUrl, "base-address", EntityVocabulary.Medium);
                }
            }
            else if (e.Type == EntityVocabulary.Queue)
            {
                if (e.Name.Length > 2 && e.Name[0] == '{' && e.Name[^1] == '}')
                {
                    var key = e.Name[1..^1];
                    if (settings.TryGetValue(GraphMerger.KeyOf(key), out var nameSetting))
                    {
                        SetResolved(e, valuesOf[nameSetting]);
                        Configure(nameSetting, e, RuleConfigName, "name", EntityVocabulary.High);
                    }
                }
                else if (e.Name.Length >= 4)
                {
                    foreach (var s in settings.Values)
                        if (valuesOf[s].Any(v => v.Value.Equals(e.Name, StringComparison.OrdinalIgnoreCase)))
                            Configure(s, e, RuleConfigName, "name", EntityVocabulary.Medium);
                }
            }
        }
    }

    private static void SetResolved(MergedEntity e, IReadOnlyList<SettingValue> values)
    {
        if (values.Count == 0) return;
        e.Attrs["resolved"] = JsonSerializer.SerializeToElement(values.Select(v => new { file = v.File, value = v.Value }).ToList(), Json);
    }

    private static List<SettingValue> ValuesOf(MergedEntity setting)
    {
        var list = new List<SettingValue>();
        if (!setting.Attrs.TryGetValue("values", out var v) || v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var file = item.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
            var value = item.TryGetProperty("value", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
            if (value.Length > 0) list.Add(new SettingValue(file, value));
        }
        return list;
    }

    private static string? StringAttr(MergedEntity e, string name) =>
        e.Attrs.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? OriginOf(string value)
    {
        var m = Origin.Match(value.Trim());
        return m.Success ? m.Groups["o"].Value.TrimEnd('/') : null;
    }

    // ── mentions ────────────────────────────────────────────────────────

    private sealed record Alias(MergedEntity Entity, string? Via);

    private static void Mention(MergedGraph graph, IReadOnlyList<NarrativeChunk> narrative)
    {
        if (narrative.Count == 0) return;

        var names = new Dictionary<string, List<Alias>>(StringComparer.Ordinal);
        var exact = new Dictionary<string, List<MergedEntity>>(StringComparer.Ordinal);
        foreach (var e in graph.Entities.Where(e => !NotMentioned.Contains(e.Type)))
        {
            foreach (var (alias, via) in AliasesOf(e))
            {
                if (!Distinctive(alias)) continue;
                var key = alias.ToLowerInvariant();
                if (!names.TryGetValue(key, out var list)) names[key] = list = new List<Alias>();
                if (!list.Any(a => ReferenceEquals(a.Entity, e))) list.Add(new Alias(e, via));
            }
            if (e.Type is EntityVocabulary.Job or EntityVocabulary.Package or EntityVocabulary.Class or EntityVocabulary.Procedure
                or EntityVocabulary.Table or EntityVocabulary.View or EntityVocabulary.Function)
            {
                // By its name, and for a qualified name (a class in a namespace, a table in a schema) by its last part.
                foreach (var (alias, via) in AliasesOf(e))
                {
                    if (via is not null) continue;
                    var key = alias.ToLowerInvariant();
                    if (!exact.TryGetValue(key, out var list)) exact[key] = list = new List<MergedEntity>();
                    if (!list.Contains(e)) list.Add(e);
                }
            }
        }
        if (names.Count == 0 && exact.Count == 0) return;

        var found = new Dictionary<MergedEntity, List<(string Id, string Kind, string? Via)>>(ReferenceEqualityComparer.Instance);
        foreach (var chunk in narrative)
        {
            var hits = new Dictionary<MergedEntity, string?>(ReferenceEqualityComparer.Instance);

            // A ticket's configuration item is a structured field: an exact match on it counts without being distinctive.
            if (chunk.Kind == "ticket" && ConfigurationItem.Match(chunk.Text) is { Success: true } ci
                && exact.TryGetValue(GraphMerger.KeyOf(ci.Groups["v"].Value), out var named))   // KeyOf lower-cases
            {
                foreach (var e in named) hits[e] = "cmdb_ci";
            }

            foreach (Match m in Token.Matches(chunk.Text))
            {
                foreach (var alias in Lookup(names, m.Value.ToLowerInvariant()))
                    hits.TryAdd(alias.Entity, alias.Via);
            }

            foreach (var (entity, via) in hits)
            {
                if (!found.TryGetValue(entity, out var list)) found[entity] = list = new List<(string, string, string?)>();
                list.Add((chunk.ShortId, chunk.Kind, via));
            }
        }

        foreach (var (entity, mentions) in found)
        {
            var kept = mentions.GroupBy(m => m.Kind).SelectMany(g => g.Take(MaxMentionsPerKind)).ToList();
            entity.Attrs["mentions"] = JsonSerializer.SerializeToElement(
                kept.Select(m => new { evidenceId = m.Id, kind = m.Kind, via = m.Via }).ToList(), new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
            entity.Attrs["mentionCounts"] = JsonSerializer.SerializeToElement(
                mentions.GroupBy(m => m.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
        }
    }

    /// <summary>A token's own name, else (for <c>dbo.TLR_STAGE</c>) what follows each dot.</summary>
    private static IEnumerable<Alias> Lookup(Dictionary<string, List<Alias>> names, string token)
    {
        if (names.TryGetValue(token, out var direct)) return direct;
        var dot = token.IndexOf('.');
        while (dot >= 0 && dot < token.Length - 1)
        {
            var tail = token[(dot + 1)..];
            if (names.TryGetValue(tail, out var viaTail)) return viaTail;
            dot = token.IndexOf('.', dot + 1);
        }
        return Array.Empty<Alias>();
    }

    private static IEnumerable<(string Alias, string? Via)> AliasesOf(MergedEntity e)
    {
        yield return (e.Name, null);
        switch (e.Type)
        {
            case EntityVocabulary.Class or EntityVocabulary.Table or EntityVocabulary.View or EntityVocabulary.Procedure or EntityVocabulary.Function:
                if (e.Name.Contains('.')) yield return (e.Name[(e.Name.LastIndexOf('.') + 1)..], null);
                if (e.Type == EntityVocabulary.Table)
                    foreach (var constraint in ConstraintNames(e)) yield return (constraint, "constraint " + constraint);
                break;
            case EntityVocabulary.ConfigKey:
                if (e.Name.Contains(':')) yield return (e.Name.Replace(':', '.'), null);
                break;
        }
    }

    private static IEnumerable<string> ConstraintNames(MergedEntity table)
    {
        foreach (var attr in new[] { "uniqueKeys", "foreignKeys" })
        {
            if (!table.Attrs.TryGetValue(attr, out var v) || v.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
                    yield return name;
        }
    }

    /// <summary>
    /// A name specific enough that finding it in a sentence means the thing: it has an underscore, a digit,
    /// a colon, a dot between names, or the capitals of a compound name. A plain word such as "Drawer" is not.
    /// </summary>
    public static bool Distinctive(string alias)
    {
        if (alias.Length < 5) return false;
        if (alias.Contains('_') || alias.Contains(':') || alias.Any(char.IsDigit)) return true;
        if (alias.Contains('.') && alias.Length >= 6) return true;
        var upper = alias.Count(char.IsUpper);
        var lower = alias.Count(char.IsLower);
        return (upper >= 2 && lower >= 1) || (upper >= 6 && lower == 0);
    }
}
