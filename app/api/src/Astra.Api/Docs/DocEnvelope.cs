using System.Text.Json;

namespace Astra.Api.Docs;

/// <summary>
/// The <c>{ markdown, meta }</c> envelope every model-authored document
/// comes back in. <see cref="Markdown"/> is the document; <see cref="Meta"/>
/// is the structured sidecar the UI, drift detection and the requirements
/// coverage/delivery reports read. Persisted flattened —
/// <c>{ ...meta, ...extras, markdown }</c> — so existing readers that look
/// for top-level fields such as <c>ruleText</c>, <c>statement</c>,
/// <c>sourceRoutines</c>, <c>title</c> or <c>subsystems</c> keep working.
/// </summary>
public sealed class DocEnvelope
{
    public string Markdown { get; }
    public JsonElement Meta { get; }

    private DocEnvelope(string markdown, JsonElement meta)
    {
        Markdown = markdown;
        Meta = meta;
    }

    /// <summary>Parse a tool input of the shape <c>{ markdown, meta }</c>.
    /// Throws when <c>markdown</c> is missing or blank — an empty document is
    /// a failed call, not a section.</summary>
    public static DocEnvelope Parse(string toolInputJson)
    {
        using var doc = JsonDocument.Parse(toolInputJson);
        return FromElement(doc.RootElement);
    }

    public static DocEnvelope FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Document envelope must be a JSON object, got {root.ValueKind}.");
        if (!root.TryGetProperty("markdown", out var md)
            || md.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(md.GetString()))
            throw new InvalidOperationException("Document envelope has no non-empty 'markdown' field.");

        var meta = root.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object
            ? m.Clone()
            : EmptyObject();
        return new DocEnvelope(DocMarkdown.Normalize(md.GetString()!), meta);
    }

    /// <summary>Parse a catalogue tool input <c>{ entries: [ {markdown, meta}, … ] }</c>.
    /// Entries without a usable markdown field are skipped and counted.</summary>
    public static (IReadOnlyList<DocEnvelope> Entries, int Skipped) ParseEntries(string toolInputJson)
    {
        using var doc = JsonDocument.Parse(toolInputJson);
        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array) arr = e;
        else return (Array.Empty<DocEnvelope>(), 0);

        var list = new List<DocEnvelope>();
        var skipped = 0;
        foreach (var item in arr.EnumerateArray())
        {
            try { list.Add(FromElement(item)); }
            catch (InvalidOperationException) { skipped++; }
        }
        return (list, skipped);
    }

    public DocEnvelope WithMarkdown(string markdown) => new(DocMarkdown.Normalize(markdown), Meta);

    public string? MetaString(string prop) =>
        Meta.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public IReadOnlyList<string> MetaStrings(string prop)
    {
        var list = new List<string>();
        if (!Meta.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }

    /// <summary>Flatten to the DocSection payload: meta fields at the top
    /// level, then <paramref name="extras"/> (which win on collision), then
    /// <c>markdown</c>.</summary>
    public string ToPayloadJson(IReadOnlyDictionary<string, object?>? extras = null)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in Meta.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        if (extras is not null)
            foreach (var kv in extras)
                dict[kv.Key] = kv.Value;
        dict["markdown"] = Markdown;
        return JsonSerializer.Serialize(dict);
    }

    private static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }
}
