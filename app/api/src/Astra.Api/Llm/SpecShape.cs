using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Llm.Schemas;

namespace Astra.Api.Llm;

/// <summary>
/// What a model's reply must look like to count as a spec. The extraction tool's input schema is deliberately open
/// (the prompt defines the shape), so a reply can be valid JSON and still not be a spec: seen with a real model, the
/// whole object nested under an <c>additionalProperties</c> or <c>spec</c> key, once as text, which stored a draft
/// that showed no claims and could be signed.
///
/// Two rules, both pure: <see cref="Unwrap"/> lifts a nested spec back to the top, and <see cref="HasClaims"/> says
/// whether there is anything for a reviewer to decide. A reply without claims is not saved as a draft, and a stored
/// spec without claims cannot be signed.
/// </summary>
public static class SpecShape
{
    private const string OpenObjectKey = "additionalProperties";

    /// <summary>Keys a real model has put the whole spec under.</summary>
    private static readonly string[] WrapperKeys = { OpenObjectKey, "spec" };

    /// <summary>The sections every language shares: the ones sign-off checks.</summary>
    private static readonly string[] SharedSections = { "invariants", "side_effects", "edge_cases", "open_questions" };

    /// <summary>
    /// The spec itself, when the reply nested it under a wrapper key; otherwise the reply unchanged. Only a reply with
    /// no claim section of its own is touched, and only for <c>additionalProperties</c>, <c>spec</c> or a sole
    /// property, so a well-formed spec is never rewritten. The nested spec may be an object or that object written
    /// out as text; text that is not valid JSON is left alone (and so refused for having no claims), never repaired.
    /// Fields already at the top are kept.
    /// </summary>
    public static JsonElement Unwrap(JsonElement reply, SpecSchema? schema = null)
    {
        for (var depth = 0; depth < 3; depth++)
        {
            if (reply.ValueKind != JsonValueKind.Object || HasClaims(reply, schema)) return reply;

            var properties = reply.EnumerateObject().ToList();
            var candidates = properties.Where(p => WrapperKeys.Contains(p.Name) || properties.Count == 1);
            string? wrapperName = null;
            JsonElement nested = default;
            foreach (var p in candidates)
            {
                if (!TryReadObject(p.Value, out var inner) || !LooksLikeSpec(inner, schema)) continue;
                wrapperName = p.Name;
                nested = inner;
                break;
            }
            if (wrapperName is null) return reply;

            var lifted = new JsonObject();
            foreach (var p in properties.Where(p => p.Name != wrapperName))
                lifted[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            foreach (var p in nested.EnumerateObject())
                if (!lifted.ContainsKey(p.Name)) lifted[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            reply = JsonSerializer.SerializeToElement(lifted);
        }
        return reply;
    }

    private static bool TryReadObject(JsonElement value, out JsonElement result)
    {
        result = value;
        if (value.ValueKind == JsonValueKind.Object) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString()!.Trim();
        if (!text.StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            result = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// At least one entry in a claim section: the shared four, or any section the language's schema declares.
    /// camelCase spellings count too, so a spec is never refused over a key's casing.
    /// </summary>
    public static bool HasClaims(JsonElement spec, SpecSchema? schema = null)
    {
        if (spec.ValueKind != JsonValueKind.Object) return false;
        foreach (var section in Sections(schema))
            if (spec.TryGetProperty(section, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                return true;
        return false;
    }

    private static bool LooksLikeSpec(JsonElement candidate, SpecSchema? schema) =>
        HasClaims(candidate, schema)
        || candidate.TryGetProperty("routine", out _)
        || candidate.TryGetProperty(OpenObjectKey, out _);

    private static IEnumerable<string> Sections(SpecSchema? schema)
    {
        var declared = schema?.ClaimKinds.Select(k => k.SpecJsonField).Where(f => !string.IsNullOrWhiteSpace(f))
                       ?? Enumerable.Empty<string>();
        foreach (var section in SharedSections.Concat(declared))
        {
            yield return section;
            var camel = Camel(section);
            if (camel != section) yield return camel;
        }
    }

    private static string Camel(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? snake : parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
