using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Evidence;

/// <summary>A paragraph or line of a document: body text, or a heading of a given level (1 is the top).</summary>
internal sealed record DocBlock(int Level, string Text, int? Line, string? Heading = null);

/// <summary>
/// Cuts a document into sections, one chunk per heading (<c>h=Restart</c>): the heading with everything up
/// to the next one. A section longer than a page is split at a paragraph boundary into parts. Text before
/// the first heading is the <c>Preamble</c>. A heading with nothing under it has nothing to cite and is
/// left out; its text is still in the path of the sections below it.
/// </summary>
internal static class SectionChunker
{
    public const int MaxSectionChars = 5000;
    public const int MaxHeadingChars = 80;

    public static List<EvidenceChunkDraft> Chunk(IReadOnlyList<DocBlock> blocks)
    {
        var drafts = new List<EvidenceChunkDraft>();
        var trail = new List<(int Level, string Text)>();
        DocBlock? heading = null;
        var path = Array.Empty<string>();
        var body = new List<DocBlock>();

        void Flush()
        {
            if (body.All(b => string.IsNullOrWhiteSpace(b.Text))) { body.Clear(); return; }
            EmitSection(drafts, heading, path, body);
            body.Clear();
        }

        foreach (var b in blocks)
        {
            if (b.Level > 0 && !string.IsNullOrWhiteSpace(b.Heading))
            {
                Flush();
                while (trail.Count > 0 && trail[^1].Level >= b.Level) trail.RemoveAt(trail.Count - 1);
                trail.Add((b.Level, b.Heading!));
                heading = b;
                path = trail.Select(t => t.Text).ToArray();
            }
            else
            {
                body.Add(b);
            }
        }
        Flush();
        return drafts;
    }

    private static void EmitSection(List<EvidenceChunkDraft> drafts, DocBlock? heading, string[] path, List<DocBlock> body)
    {
        var parts = new List<List<DocBlock>>();
        var current = new List<DocBlock>();
        var chars = heading?.Text.Length ?? 0;
        foreach (var b in body)
        {
            if (current.Count > 0 && chars + b.Text.Length + 1 > MaxSectionChars)
            {
                parts.Add(current);
                current = new List<DocBlock>();
                chars = 0;
            }
            current.Add(b);
            chars += b.Text.Length + 1;
        }
        if (current.Count > 0) parts.Add(current);

        // Blank blocks at the edges of a part are not part of what it says.
        parts = parts.Select(Trimmed).Where(p => p.Count > 0).ToList();

        for (var p = 0; p < parts.Count; p++)
        {
            var blocks = new List<DocBlock>();
            if (p == 0 && heading is not null) blocks.Add(heading);
            blocks.AddRange(parts[p]);

            var title = heading?.Heading ?? "Preamble";
            var label = string.Join(" › ", path.Length > 0 ? path.TakeLast(3) : new[] { title });
            if (heading is null) label = "Preamble";
            if (parts.Count > 1) label += $" (part {p + 1} of {parts.Count})";

            var lines = blocks.Where(b => b.Line is not null).Select(b => b.Line!.Value).ToList();
            drafts.Add(new EvidenceChunkDraft(
                "h=" + LocatorHeading(title),
                label,
                string.Join('\n', blocks.Select(b => b.Text)),
                AdapterJson.Object(w =>
                {
                    w.WriteString("heading", heading is null ? "Preamble" : title);
                    w.WriteNumber("level", heading?.Level ?? 0);
                    w.WriteStartArray("path");
                    foreach (var s in path) w.WriteStringValue(s);
                    w.WriteEndArray();
                    if (parts.Count > 1)
                    {
                        w.WriteNumber("part", p + 1);
                        w.WriteNumber("parts", parts.Count);
                    }
                }),
                lines.Count > 0 ? lines.Min() : null,
                lines.Count > 0 ? lines.Max() : null));
        }
    }

    private static List<DocBlock> Trimmed(List<DocBlock> blocks)
    {
        var from = 0;
        var to = blocks.Count - 1;
        while (from <= to && string.IsNullOrWhiteSpace(blocks[from].Text)) from++;
        while (to >= from && string.IsNullOrWhiteSpace(blocks[to].Text)) to--;
        return blocks.GetRange(from, Math.Max(0, to - from + 1));
    }

    public static string LocatorHeading(string heading)
    {
        var s = Regex.Replace(heading, @"\s+", " ").Trim();
        if (s.Length == 0) return "(untitled)";
        return s.Length <= MaxHeadingChars ? s : s[..MaxHeadingChars].TrimEnd();
    }
}

/// <summary>Markdown notes, cut at ATX headings (<c># Title</c>) outside code fences. Lines are kept.</summary>
public sealed class MarkdownAdapter : IEvidenceAdapter
{
    /// <summary>The longest a section's text may be before it is split into parts (Markdown and Word alike).</summary>
    public const int SectionChars = SectionChunker.MaxSectionChars;

    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;

        var lines = input.Text.TrimStart('﻿').Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var blocks = new List<DocBlock>(count);
        var fence = (string?)null;
        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
                blocks.Add(new DocBlock(0, line, i + 1));
                continue;
            }
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                blocks.Add(new DocBlock(0, line, i + 1));
                continue;
            }

            var m = Heading.Match(line);
            blocks.Add(m.Success
                ? new DocBlock(m.Groups[1].Length, line, i + 1, PlainHeading(m.Groups[2].Value))
                : new DocBlock(0, line, i + 1));
        }
        return new AdapterResult(SectionChunker.Chunk(blocks), Array.Empty<string>());
    }

    // Underscores are left alone: in this kind of document they are far more often part of a name
    // (TLR_EOD_LOCK) than emphasis, and a locator must keep the name as written.
    private static string PlainHeading(string raw)
    {
        var s = Link.Replace(raw, "$1");
        return Regex.Replace(s, @"[`*]+", "").Trim();
    }
}

/// <summary>
/// A Word file (.docx), read straight from its package: no Office library, no external resources. Headings
/// are found by their style (Heading 1…9, Title, or any style with an outline level), tables become one
/// line per row with the cells joined by <c>|</c>, list items start with <c>-</c>. Comments, headers and
/// footers, and deleted text are not read. Word files have no lines, so a chunk is shown by its text.
/// </summary>
public sealed class DocxAdapter : IEvidenceAdapter
{
    public const long MaxXmlBytes = 32L * 1024 * 1024;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly Regex HeadingStyle = new(@"^heading\s*(\d)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record StyleInfo(string? Name, int? Outline, string? BasedOn);

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Bytes is null) return AdapterResult.Empty;

        XDocument document;
        Dictionary<string, StyleInfo> styles;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(input.Bytes, writable: false), ZipArchiveMode.Read);
            var main = zip.GetEntry("word/document.xml");
            if (main is null) return AdapterResult.Nothing("This is not a Word document: it has no word/document.xml.");
            document = Load(main);
            styles = zip.GetEntry("word/styles.xml") is { } s ? ReadStyles(Load(s)) : new Dictionary<string, StyleInfo>();
        }
        catch (InvalidDataException)
        {
            return AdapterResult.Nothing("This is not a readable Word document (the file is not a valid .docx package).");
        }
        catch (XmlException ex)
        {
            return AdapterResult.Nothing($"The document's XML could not be read ({AdapterText.Snippet(ex.Message, 120)}).");
        }
        catch (IOException ex) when (ex.Message == TooBig)
        {
            return AdapterResult.Nothing("The document is too large to read safely once unpacked.");
        }

        var body = document.Root?.Element(W + "body");
        if (body is null) return AdapterResult.Nothing("The document has no body.");

        var blocks = new List<DocBlock>();
        foreach (var e in body.Elements()) ReadBlock(e, styles, blocks);
        return new AdapterResult(SectionChunker.Chunk(blocks), Array.Empty<string>());
    }

    private const string TooBig = "docx part too large";

    private static XDocument Load(ZipArchiveEntry entry)
    {
        // The size in the zip header can be a lie: read at most the limit and stop.
        using var raw = entry.Open();
        using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = raw.Read(buffer, 0, buffer.Length)) > 0)
        {
            bounded.Write(buffer, 0, n);
            if (bounded.Length > MaxXmlBytes) throw new IOException(TooBig);
        }
        bounded.Position = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000_000 };
        using var reader = XmlReader.Create(bounded, settings);
        return XDocument.Load(reader);
    }

    private static Dictionary<string, StyleInfo> ReadStyles(XDocument doc)
    {
        var map = new Dictionary<string, StyleInfo>(StringComparer.Ordinal);
        foreach (var s in doc.Descendants(W + "style"))
        {
            var id = (string?)s.Attribute(W + "styleId");
            if (string.IsNullOrEmpty(id)) continue;
            int? outline = int.TryParse((string?)s.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val"), out var o) ? o : null;
            map[id] = new StyleInfo((string?)s.Element(W + "name")?.Attribute(W + "val"), outline, (string?)s.Element(W + "basedOn")?.Attribute(W + "val"));
        }
        return map;
    }

    private static void ReadBlock(XElement e, Dictionary<string, StyleInfo> styles, List<DocBlock> blocks)
    {
        if (e.Name == W + "p")
        {
            var text = ParagraphText(e);
            if (text.Length == 0) return;

            var level = HeadingLevel(e, styles);
            if (level > 0)
            {
                blocks.Add(new DocBlock(level, text, null, Regex.Replace(text, @"\s+", " ").Trim()));
                return;
            }
            var pPr = e.Element(W + "pPr");
            if (pPr?.Element(W + "numPr") is { } num)
            {
                var depth = int.TryParse((string?)num.Element(W + "ilvl")?.Attribute(W + "val"), out var d) ? Math.Clamp(d, 0, 6) : 0;
                text = new string(' ', depth * 2) + "- " + text;
            }
            blocks.Add(new DocBlock(0, text, null));
        }
        else if (e.Name == W + "tbl")
        {
            foreach (var row in e.Descendants(W + "tr"))
            {
                var cells = row.Elements(W + "tc")
                    .Select(tc => string.Join(' ', tc.Descendants(W + "p").Select(ParagraphText).Where(t => t.Length > 0)))
                    .ToList();
                var line = string.Join(" | ", cells);
                if (!string.IsNullOrWhiteSpace(line.Replace("|", ""))) blocks.Add(new DocBlock(0, line, null));
            }
        }
        else if (e.Name == W + "sdt")
        {
            foreach (var inner in e.Element(W + "sdtContent")?.Elements() ?? Enumerable.Empty<XElement>())
                ReadBlock(inner, styles, blocks);
        }
    }

    private static string ParagraphText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var n in p.Descendants())
        {
            if (n.Name == W + "t") sb.Append(n.Value);
            else if (n.Name == W + "tab") sb.Append('\t');
            else if (n.Name == W + "br" || n.Name == W + "cr") sb.Append('\n');
            else if (n.Name == W + "noBreakHyphen") sb.Append('-');
        }
        return sb.ToString().Trim();
    }

    /// <summary>1–9 for a heading, 0 for body text.</summary>
    private static int HeadingLevel(XElement p, Dictionary<string, StyleInfo> styles)
    {
        var pPr = p.Element(W + "pPr");
        if (int.TryParse((string?)pPr?.Element(W + "outlineLvl")?.Attribute(W + "val"), out var direct))
            return direct is >= 0 and <= 8 ? direct + 1 : 0;

        var id = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val");
        for (var hop = 0; id is not null && hop < 6; hop++)
        {
            if (styles.TryGetValue(id, out var style))
            {
                var level = FromName(style.Name);
                if (level > 0) return level;
                if (style.Outline is >= 0 and <= 8) return style.Outline.Value + 1;
                id = style.BasedOn;
            }
            else
            {
                return FromName(id);
            }
        }
        return 0;
    }

    private static int FromName(string? name)
    {
        if (name is null) return 0;
        if (name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        var m = HeadingStyle.Match(name.Trim());
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
