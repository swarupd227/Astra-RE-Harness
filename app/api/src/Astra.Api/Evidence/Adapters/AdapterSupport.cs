using System.Text;
using System.Text.Json;

namespace Astra.Api.Evidence;

/// <summary>
/// Maps an offset in a file to its 1-based line, for parsers that report where a token starts in
/// bytes rather than in lines, so a piece of evidence can still point at the lines it came from.
/// </summary>
internal sealed class LineIndex
{
    private readonly List<int> _starts = new() { 0 };

    private LineIndex() { }

    public static LineIndex Of(ReadOnlySpan<byte> bytes)
    {
        var index = new LineIndex();
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n') index._starts.Add(i + 1);
        return index;
    }

    public int LineAt(long offset)
    {
        var at = _starts.BinarySearch((int)Math.Clamp(offset, 0, int.MaxValue));
        return (at >= 0 ? at : ~at - 1) + 1;
    }
}

internal static class AdapterJson
{
    /// <summary>Writes a JSON object whose property order is the order written (a serialiser would reorder).</summary>
    public static string Object(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void Optional(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) w.WriteString(name, value);
    }

    /// <summary>A list of strings, left out when there is nothing in it.</summary>
    public static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var v in list) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}

internal static class AdapterText
{
    /// <summary>One line of text for a label: whitespace collapsed, cut at a word where it can be.</summary>
    public static string Snippet(string text, int max)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max - 1);
        return s[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…";
    }

    public static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
