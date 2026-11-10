using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Evidence;

/// <summary>
/// Cuts an XML file into the elements that can be cited, whatever the vocabulary: a screen definition, a
/// job, a package task. An element that fits on a page (60 lines) is one chunk, located by its path
/// (<c>xpath:/JOBP/Tasks/Task[3]</c>); a larger one is opened up and its children become chunks, with its
/// own opening tag as a chunk of its own. A run of tiny same-named siblings (a hundred
/// <c>&lt;Param/&gt;</c>) is one chunk located by a range (<c>Param[1..12]</c>). A kind that knows its
/// vocabulary (job schedules, ETL packages) can add facts on top; this is what every XML file gets.
/// <para>
/// DTDs and external entities are refused. XML squeezed onto a few lines is re-indented first so a chunk
/// is a piece of text, not the whole file; its chunks then carry no line numbers, because the lines would
/// be those of the re-indented text, not of the file as stored.
/// </para>
/// </summary>
public sealed class XmlElementAdapter : IEvidenceAdapter
{
    public const int TargetLines = 60;
    public const int MaxUnitChars = 6000;
    public const int TinyLeafLines = 3;
    public const int MaxElements = 200_000;
    private const int MaxAttributes = 12;

    private static readonly string[] NameAttributes = { "Name", "name", "Id", "id", "ID", "Label", "label", "Key", "key", "Title", "title" };

    private readonly Func<string, int, bool> _alwaysOpen;
    private readonly Func<string, bool> _keepApart;

    public XmlElementAdapter() : this(null, null) { }

    /// <param name="alwaysOpen">Elements (by local name and depth, the root being 0) opened up whatever their size, so their children are the chunks.</param>
    /// <param name="keepApart">Elements (by local name) that are never merged with their same-named neighbours, however tiny: each is a chunk of its own.</param>
    public XmlElementAdapter(Func<string, int, bool>? alwaysOpen, Func<string, bool>? keepApart)
    {
        _alwaysOpen = alwaysOpen ?? ((_, _) => false);
        _keepApart = keepApart ?? (_ => false);
    }

    private sealed class XNode
    {
        public int Depth;
        public string Name = "";
        public readonly List<KeyValuePair<string, string>> Attributes = new();
        public int Start;
        public int End;
        public XNode? Parent;
        public readonly List<XNode> Children = new();
        public int Position;
        public int SameNamed;
    }

    private enum UnitKind { Whole, Head, Run }

    private sealed record Unit(XNode Node, UnitKind Kind, int From, int To, XNode? Last = null);

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;
        var text = input.Text.TrimStart('﻿');

        XNode? root;
        try
        {
            root = Parse(text);
        }
        catch (XmlException ex)
        {
            var fallback = LineWindowAdapter.Instance.Build(input);
            var warnings = new List<string>
            {
                $"This is not well-formed XML ({AdapterText.Snippet(ex.Message, 120)}), so it was indexed by line windows and a citation points at lines, not at an element.",
            };
            warnings.AddRange(fallback.Warnings);
            return new AdapterResult(fallback.Chunks, warnings);
        }
        if (root is null) return AdapterResult.Empty;

        var lines = text.Split('\n');
        var units = Units(root, lines);
        if (Overlap(units))
        {
            // Several elements share a line: the file is minified. Cut a re-indented copy instead.
            string pretty;
            try { pretty = XDocument.Parse(text).ToString(); }
            catch (XmlException) { return LineWindowAdapter.Instance.Build(input); }
            var prettyRoot = Parse(pretty);
            if (prettyRoot is null) return AdapterResult.Empty;
            var prettyLines = pretty.Split('\n');
            return new AdapterResult(
                ToDrafts(Units(prettyRoot, prettyLines), prettyLines, withLines: false),
                new[] { "This XML is squeezed onto a few lines, so a citation shows it re-indented rather than as stored." });
        }
        return new AdapterResult(ToDrafts(units, lines, withLines: true), Array.Empty<string>());
    }

    // ── reading ─────────────────────────────────────────────────────────

    private static XNode? Parse(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 64_000_000,
        };
        using var reader = new StringReader(text);
        using var xml = XmlReader.Create(reader, settings);
        var at = (IXmlLineInfo)xml;

        XNode? root = null;
        var open = new Stack<XNode>();
        var count = 0;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                if (++count > MaxElements) throw new XmlException($"more than {MaxElements:N0} elements");
                var node = new XNode { Name = xml.LocalName, Start = at.LineNumber, Parent = open.Count > 0 ? open.Peek() : null, Depth = open.Count };
                var last = node.Start;
                var empty = xml.IsEmptyElement;
                if (xml.HasAttributes)
                {
                    while (xml.MoveToNextAttribute())
                    {
                        last = at.LineNumber;
                        if (xml.Prefix == "xmlns" || xml.Name == "xmlns") continue;
                        if (node.Attributes.Count < MaxAttributes) node.Attributes.Add(new(xml.LocalName, xml.Value));
                    }
                    xml.MoveToElement();
                }
                node.End = last;
                node.Parent?.Children.Add(node);
                root ??= node;
                if (!empty) open.Push(node);
            }
            else if (xml.NodeType == XmlNodeType.EndElement)
            {
                open.Pop().End = at.LineNumber;
            }
        }
        if (root is not null) Number(root);
        return root;
    }

    /// <summary>Gives each element its place among same-named siblings, for the path steps (<c>Task[3]</c>).</summary>
    private static void Number(XNode root)
    {
        var pending = new Stack<XNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var n = pending.Pop();
            foreach (var group in n.Children.GroupBy(c => c.Name))
            {
                var all = group.ToList();
                for (var i = 0; i < all.Count; i++)
                {
                    all[i].Position = i + 1;
                    all[i].SameNamed = all.Count;
                }
            }
            foreach (var c in n.Children) pending.Push(c);
        }
        root.Position = 1;
        root.SameNamed = 1;
    }

    // ── choosing the pieces ─────────────────────────────────────────────

    private List<Unit> Units(XNode root, string[] lines)
    {
        var units = new List<Unit>();
        var prefix = new long[lines.Length + 1];
        for (var i = 0; i < lines.Length; i++) prefix[i + 1] = prefix[i] + lines[i].Length + 1;
        long CharsOf(int from, int to) => prefix[Math.Min(to, lines.Length)] - prefix[Math.Max(from, 1) - 1];

        void Visit(XNode n)
        {
            var size = n.End - n.Start + 1;
            if (n.Children.Count == 0 || (!_alwaysOpen(n.Name, n.Depth) && size <= TargetLines && CharsOf(n.Start, n.End) <= MaxUnitChars))
            {
                units.Add(new Unit(n, UnitKind.Whole, n.Start, n.End));
                return;
            }
            var first = n.Children[0];
            if (first.Start > n.Start) units.Add(new Unit(n, UnitKind.Head, n.Start, first.Start - 1));
            Children(n);
        }

        void Children(XNode parent)
        {
            List<XNode>? run = null;
            void Flush()
            {
                if (run is null) return;
                units.Add(run.Count == 1
                    ? new Unit(run[0], UnitKind.Whole, run[0].Start, run[0].End)
                    : new Unit(run[0], UnitKind.Run, run[0].Start, run[^1].End, run[^1]));
                run = null;
            }

            foreach (var c in parent.Children)
            {
                var tiny = c.Children.Count == 0 && c.End - c.Start + 1 <= TinyLeafLines && !_keepApart(c.Name);
                if (!tiny)
                {
                    Flush();
                    Visit(c);
                    continue;
                }
                if (run is not null && (run[0].Name != c.Name || c.End - run[0].Start + 1 > TargetLines)) Flush();
                (run ??= new List<XNode>()).Add(c);
            }
            Flush();
        }

        Visit(root);
        return units;
    }

    private static bool Overlap(List<Unit> units)
    {
        for (var i = 1; i < units.Count; i++)
            if (units[i].From <= units[i - 1].To) return true;
        return false;
    }

    // ── chunks ──────────────────────────────────────────────────────────

    private static List<EvidenceChunkDraft> ToDrafts(List<Unit> units, string[] lines, bool withLines)
    {
        var drafts = new List<EvidenceChunkDraft>(units.Count);
        foreach (var u in units)
        {
            var from = Math.Max(1, u.From);
            var to = Math.Min(lines.Length, u.To);
            if (to < from) continue;
            var text = string.Join('\n', lines[(from - 1)..to]);

            var path = PathOf(u.Node.Parent) + "/" + Step(u.Node, u.Kind == UnitKind.Run ? u.Last : null);
            var shown = Display(u.Node);
            var label = u.Kind switch
            {
                UnitKind.Run => $"{u.Node.Name} ×{u.Last!.Position - u.Node.Position + 1}",
                UnitKind.Head => $"{u.Node.Name}{(shown.Length > 0 ? " " + shown : "")} (opening)",
                _ => $"{u.Node.Name}{(shown.Length > 0 ? " " + shown : "")}",
            };

            drafts.Add(new EvidenceChunkDraft(
                "xpath:" + path,
                label,
                text,
                AdapterJson.Object(w =>
                {
                    w.WriteString("element", u.Node.Name);
                    w.WriteString("xpath", path);
                    w.WriteString("part", u.Kind.ToString().ToLowerInvariant());
                    w.WriteStartObject("attributes");
                    var used = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (k, v) in u.Node.Attributes)
                        if (used.Add(k)) w.WriteString(k, AdapterText.Clip(v, 300));
                    w.WriteEndObject();
                }),
                withLines ? from : null,
                withLines ? to : null));
        }
        return drafts;
    }

    private static string PathOf(XNode? node)
    {
        if (node is null) return "";
        var steps = new List<string>();
        for (var n = node; n is not null; n = n.Parent) steps.Add(Step(n, null));
        steps.Reverse();
        return "/" + string.Join('/', steps);
    }

    private static string Step(XNode n, XNode? runEnd)
    {
        if (runEnd is not null) return $"{n.Name}[{n.Position}..{runEnd.Position}]";
        return n.SameNamed > 1 ? $"{n.Name}[{n.Position}]" : n.Name;
    }

    private static string Display(XNode n)
    {
        foreach (var name in NameAttributes)
        {
            var hit = n.Attributes.FirstOrDefault(a => a.Key == name);
            if (!string.IsNullOrWhiteSpace(hit.Value)) return AdapterText.Clip(hit.Value.Trim(), 60);
        }
        return "";
    }
}
