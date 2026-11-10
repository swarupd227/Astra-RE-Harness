using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Evidence;

/// <summary>
/// Helpers for adapters that read facts out of an XML file whose chunks the generic XML chunker made.
/// Names are compared by local name and without regard to case (a namespace prefix or an upper-case
/// attribute is not a different thing), and a fact finds its evidence by its element's path: the chunk
/// whose path is the element itself, or the nearest one above it.
/// </summary>
internal static class XmlFacts
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex RunRange = new(@"^(?<pre>.*)\[(?<a>\d+)\.\.(?<b>\d+)\]$", RegexOptions.Compiled);

    public static XDocument Load(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000_000 };
        using var reader = XmlReader.Create(new StringReader(text.TrimStart('﻿')), settings);
        return XDocument.Load(reader);
    }

    public static string Local(XElement e) => e.Name.LocalName;

    public static bool Is(XElement e, params string[] names) =>
        names.Any(n => e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first attribute of the element with one of these names, trimmed; null if none or blank.</summary>
    public static string? Attr(XElement e, params string[] names)
    {
        foreach (var name in names)
        {
            var a = e.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (a is not null && !string.IsNullOrWhiteSpace(a.Value)) return a.Value.Trim();
        }
        return null;
    }

    /// <summary>The element's own text (CDATA included), trimmed; null if blank.</summary>
    public static string? Text(XElement e)
    {
        var t = e.Value.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>An attribute, or failing that the text of a direct child, under any of these names.</summary>
    public static string? AttrOrChild(XElement e, params string[] names) =>
        Attr(e, names) ?? e.Elements().Where(c => Is(c, names)).Select(Text).FirstOrDefault(t => t is not null);

    public static IReadOnlyList<string> Emails(string? text) =>
        text is null ? Array.Empty<string>() : Email.Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool Truthy(string? v) =>
        v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("y", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The element's path as the XML chunker writes it: names without namespaces, and an index only where
    /// the parent has more than one child of that name (<c>/JOBP/Tasks/Task[3]</c>).
    /// </summary>
    public static string XPathOf(XElement element)
    {
        var steps = new List<string>();
        for (var e = element; e is not null; e = e.Parent)
        {
            var local = e.Name.LocalName;
            var same = e.Parent?.Elements().Where(x => x.Name.LocalName == local).ToList();
            steps.Add(same is { Count: > 1 } ? $"{local}[{same.IndexOf(e) + 1}]" : local);
        }
        steps.Reverse();
        return "/" + string.Join('/', steps);
    }

    /// <summary>The locator of the chunk that shows this path: its own chunk, else the closest one above it.</summary>
    public static string? ChunkFor(IReadOnlyList<EvidenceChunkDraft> chunks, string xpath)
    {
        string? best = null;
        var bestLength = -1;
        foreach (var c in chunks)
        {
            if (!c.Locator.StartsWith("xpath:", StringComparison.Ordinal)) continue;
            var path = c.Locator["xpath:".Length..];
            var tilde = path.IndexOf('~');
            if (tilde > 0) path = path[..tilde];
            if (Covers(path, xpath) && path.Length > bestLength)
            {
                best = c.Locator;
                bestLength = path.Length;
            }
        }
        return best;
    }

    private static bool Covers(string chunkPath, string xpath)
    {
        if (xpath == chunkPath || xpath.StartsWith(chunkPath + "/", StringComparison.Ordinal)) return true;

        // A run of tiny siblings, Param[1..40], covers Param[7] and everything under it.
        var run = RunRange.Match(chunkPath);
        if (!run.Success) return false;
        var prefix = run.Groups["pre"].Value + "[";
        if (!xpath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = xpath[prefix.Length..];
        var close = rest.IndexOf(']');
        if (close < 0 || !int.TryParse(rest[..close], out var index)) return false;
        return index >= int.Parse(run.Groups["a"].Value) && index <= int.Parse(run.Groups["b"].Value)
               && (rest.Length == close + 1 || rest[close + 1] == '/');
    }
}
