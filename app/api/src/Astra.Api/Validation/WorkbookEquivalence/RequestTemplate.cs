using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.WorkbookEquivalence;

/// <summary>
/// An <c>outside_requests</c> body template rendered for one case, and the comparison of what the package actually
/// sent with it. A string leaf <c>"{field}"</c> is that field's value (numbers stay numbers); an array item
/// <c>"...{field}"</c> spreads a selection's ids; everything else is a constant. Pure.
/// </summary>
public static class RequestTemplate
{
    private static readonly Regex Whole = new(@"^\{(?<f>[A-Za-z_][\w]*)\}$", RegexOptions.Compiled);
    private static readonly Regex Spread = new(@"^\.\.\.\{(?<f>[A-Za-z_][\w]*)\}$", RegexOptions.Compiled);

    /// <summary>The body the source would send. <paramref name="missing"/> collects placeholders with no value.</summary>
    public static JsonNode? Render(JsonNode? template, IReadOnlyDictionary<string, JsonNode?> values, List<string> missing)
    {
        switch (template)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (k, v) in o) obj[k] = Render(v, values, missing);
                return obj;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var item in a)
                {
                    if (item is JsonValue sv && sv.TryGetValue<string>(out var s) && Spread.Match(s) is { Success: true } sp)
                    {
                        if (values.TryGetValue(sp.Groups["f"].Value, out var list) && list is JsonArray ids)
                            foreach (var id in ids) arr.Add(id?.DeepClone());
                        else missing.Add(sp.Groups["f"].Value);
                        continue;
                    }
                    arr.Add(Render(item, values, missing));
                }
                return arr;
            case JsonValue val when val.TryGetValue<string>(out var str) && Whole.Match(str) is { Success: true } m:
                if (values.TryGetValue(m.Groups["f"].Value, out var v2)) return v2?.DeepClone();
                missing.Add(m.Groups["f"].Value);
                return null;
            default:
                return template?.DeepClone();
        }
    }

    /// <summary>
    /// The first place <paramref name="sent"/> differs from <paramref name="expected"/>, as "path: expected X, sent Y";
    /// null when they agree. Object key order is ignored (JSON objects are unordered); array order is not.
    /// Numbers compare by value (6666 equals 6666.0); a number sent as text is a difference.
    /// </summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? sent, string path = "$")
    {
        if (expected is null && sent is null) return null;
        if (expected is null || sent is null) return $"{path}: expected {Show(expected)}, sent {Show(sent)}";
        switch (expected)
        {
            case JsonObject eo when sent is JsonObject so:
                foreach (var (k, ev) in eo)
                {
                    if (!so.ContainsKey(k)) return $"{path}.{k}: missing from what was sent (expected {Show(ev)})";
                    if (FirstDifference(ev, so[k], $"{path}.{k}") is { } d) return d;
                }
                var extra = so.Select(kv => kv.Key).FirstOrDefault(k => !eo.ContainsKey(k));
                return extra is null ? null : $"{path}.{extra}: sent but not in the source's request ({Show(so[extra])})";
            case JsonArray ea when sent is JsonArray sa:
                for (var i = 0; i < Math.Min(ea.Count, sa.Count); i++)
                    if (FirstDifference(ea[i], sa[i], $"{path}[{i}]") is { } d) return d;
                return ea.Count == sa.Count ? null : $"{path}: expected {ea.Count} items {Show(ea)}, sent {sa.Count} {Show(sa)}";
            case JsonValue ev when sent is JsonValue sv:
                if (Number(ev) is { } a && Number(sv) is { } b && ev.GetValueKind() == JsonValueKind.Number && sv.GetValueKind() == JsonValueKind.Number)
                    return Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a)) ? null : $"{path}: expected {Show(ev)}, sent {Show(sv)}";
                return ev.ToJsonString() == sv.ToJsonString() ? null : $"{path}: expected {Show(ev)}, sent {Show(sv)}";
            default:
                return $"{path}: expected {Show(expected)}, sent {Show(sent)}";
        }
    }

    private static double? Number(JsonValue v) =>
        v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Show(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "nothing";
        return s.Length > 80 ? s[..77] + "…" : s;
    }
}
