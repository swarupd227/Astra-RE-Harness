using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Api.Knowledge;

public sealed record PackParseResult(Pack? Pack, IReadOnlyList<string> Errors)
{
    public bool Ok => Pack is not null;
}

/// <summary>
/// Reads and checks a pack's <c>pack.json</c>. A pack that is wrong is refused whole, with every reason — one
/// that half-loads would score a programme on rules nobody wrote down.
/// </summary>
public static class PackLoader
{
    private static readonly Regex Id = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex SlotKey = new("^[a-z0-9-]+:[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex Prefix = new("^[a-z0-9-]+(?::[a-z0-9-]+)*:$", RegexOptions.Compiled);

    public static PackParseResult Parse(string json)
    {
        var errors = new List<string>();
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return new PackParseResult(null, new[] { $"Not valid JSON ({ex.Message})." });
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new PackParseResult(null, new[] { "A pack must be a JSON object." });

            var id = Str(root, "id");
            if (id is null || !Id.IsMatch(id)) errors.Add("id must be lowercase letters, digits and hyphens (max 64).");
            var title = Str(root, "title");
            if (string.IsNullOrWhiteSpace(title)) errors.Add("title is required.");
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var iv) && iv >= 1 ? iv : 1;

            var vocabulary = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("vocabulary", out var vocab) && vocab.ValueKind == JsonValueKind.Object)
                foreach (var p in vocab.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        vocabulary[p.Name] = p.Value.GetString()!;

            var credit = ReadCredit(root, errors);
            var bands = ReadBands(root, errors);
            var targets = ReadTargets(root);
            var tiers = new Dictionary<string, double>(StringComparer.Ordinal);
            if (root.TryGetProperty("tierWeights", out var tw) && tw.ValueKind == JsonValueKind.Object)
                foreach (var p in tw.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var w) && w >= 0) tiers[p.Name] = w;

            var deliverables = new List<DeliverableDefinition>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("deliverables", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                errors.Add("deliverables must list at least one deliverable.");
            }
            else
            {
                foreach (var d in list.EnumerateArray())
                    if (ReadDeliverable(d, ids, keys, errors) is { } def) deliverables.Add(def);
            }

            if (errors.Count > 0) return new PackParseResult(null, errors);
            return new PackParseResult(
                new Pack(id!, title!.Trim(), version, vocabulary, credit!, bands!, targets, tiers, deliverables),
                errors);
        }
    }

    private static DeliverableDefinition? ReadDeliverable(JsonElement d, HashSet<string> ids, HashSet<string> keys, List<string> errors)
    {
        if (d.ValueKind != JsonValueKind.Object) { errors.Add("A deliverable must be an object."); return null; }
        var id = Str(d, "id");
        if (id is null || !Id.IsMatch(id)) { errors.Add($"Deliverable id '{id}' must be lowercase letters, digits and hyphens."); return null; }
        if (!ids.Add(id)) { errors.Add($"Deliverable '{id}' is listed twice."); return null; }

        var label = Str(d, "label");
        if (string.IsNullOrWhiteSpace(label)) errors.Add($"Deliverable '{id}' needs a label.");
        var weight = d.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out var wv) ? wv : -1;
        if (weight < 0 || double.IsNaN(weight) || double.IsInfinity(weight)) errors.Add($"Deliverable '{id}' needs a weight of 0 or more.");
        var generator = Str(d, "generator");
        if (string.IsNullOrWhiteSpace(generator)) errors.Add($"Deliverable '{id}' needs a generator.");

        var staticSlots = new List<SlotDefinition>();
        EntitySlots? entity = null;
        if (d.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Object)
        {
            if (slots.TryGetProperty("static", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in st.EnumerateArray())
                {
                    var key = Str(s, "key");
                    if (key is null || !SlotKey.IsMatch(key)) { errors.Add($"Slot key '{key}' in '{id}' must look like area:name."); continue; }
                    if (!keys.Add(key)) { errors.Add($"Slot '{key}' is listed twice."); continue; }
                    staticSlots.Add(new SlotDefinition(key, Str(s, "label") ?? key, Str(s, "brief")));
                }
            }
            if (slots.TryGetProperty("entity", out var en) && en.ValueKind == JsonValueKind.Object)
            {
                var prefix = Str(en, "keyPrefix");
                var countKey = Str(en, "countKey");
                if (prefix is null || !Prefix.IsMatch(prefix)) errors.Add($"Entity slots in '{id}' need a keyPrefix such as entity:job: (ending in a colon).");
                else if (!keys.Add(prefix)) errors.Add($"Entity slot prefix '{prefix}' is used twice.");
                if (countKey is null || !Id.IsMatch(countKey)) errors.Add($"Entity slots in '{id}' need a countKey (lowercase letters, digits, hyphens).");
                if (prefix is not null && countKey is not null)
                    entity = new EntitySlots(prefix, countKey, Str(en, "label") ?? prefix, Str(en, "brief"));
            }
        }
        if (staticSlots.Count == 0 && entity is null) errors.Add($"Deliverable '{id}' has no slots.");
        return new DeliverableDefinition(id, (label ?? id).Trim(), weight < 0 ? 0 : weight, generator ?? "", staticSlots, entity);
    }

    private static CreditRules? ReadCredit(JsonElement root, List<string> errors)
    {
        double a = 1.0, b = 0.5, c = 0.0;
        if (root.TryGetProperty("credit", out var el) && el.ValueKind == JsonValueKind.Object)
        {
            a = Num(el, "approvedAllSupported", a);
            b = Num(el, "approvedWithUnverified", b);
            c = Num(el, "otherwise", c);
        }
        if (a is < 0 or > 1 || b is < 0 or > 1 || c is < 0 or > 1) errors.Add("Credit values must be between 0 and 1.");
        else if (!(a >= b && b >= c)) errors.Add("Credit must not rise as evidence gets weaker (all supported ≥ with unverified ≥ otherwise).");
        return new CreditRules(a, b, c);
    }

    private static DependencyBands? ReadBands(JsonElement root, List<string> errors)
    {
        double high = 60, medium = 30;
        if (root.TryGetProperty("bands", out var el) && el.ValueKind == JsonValueKind.Object)
        {
            high = Num(el, "highAbove", high);
            medium = Num(el, "mediumFrom", medium);
        }
        if (medium < 0 || high > 100 || medium >= high) errors.Add("Bands need 0 ≤ mediumFrom < highAbove ≤ 100.");
        return new DependencyBands(high, medium);
    }

    private static DependencyTargets ReadTargets(JsonElement root)
    {
        double d90 = 50, d180 = 25, d365 = 10;
        if (root.TryGetProperty("targets", out var el) && el.ValueKind == JsonValueKind.Object)
        {
            d90 = Num(el, "day90", d90);
            d180 = Num(el, "day180", d180);
            d365 = Num(el, "day365", d365);
        }
        return new DependencyTargets(d90, d180, d365);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;
}
