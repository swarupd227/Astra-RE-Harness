using System.Text;
using System.Text.Json;

namespace Astra.Api.Signing;

/// <summary>
/// Minimal JSON canonicaliser used for sign-off hashing. Recursively sorts
/// object keys, removes whitespace, and emits compact JSON. This is a
/// pragmatic subset of RFC 8785 (JCS) — sufficient for Phase B.3 because the
/// only producer is our own server. RFC-8785-strict canonicalisation
/// (number normalisation, escape rules) lands when we publish the verifier
/// for Project 3 CI in Phase D.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, root);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteElement(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
        }
    }
}
