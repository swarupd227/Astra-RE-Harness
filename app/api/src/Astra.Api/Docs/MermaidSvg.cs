using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Docs;

/// <summary>
/// Draws the two Mermaid dialects the documentation pipeline writes (<c>sequenceDiagram</c> and <c>graph</c>/<c>flowchart</c>)
/// as a self-contained SVG, so a PDF can show the picture. The PDF engine has no JavaScript, so Mermaid itself cannot run
/// there, and sending a client's diagram to an outside renderer is not acceptable. Anything it does not understand returns
/// null and the caller keeps the diagram source instead.
/// </summary>
public static class MermaidSvg
{
    private const string Font = "font-family=\"Carlito, Calibri, 'DejaVu Sans', Arial, sans-serif\"";
    private const string Ink = "#1f2937";

    public static string? TryRender(string mermaid)
    {
        if (string.IsNullOrWhiteSpace(mermaid)) return null;
        var lines = mermaid.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("%%", StringComparison.Ordinal)).ToList();
        if (lines.Count == 0) return null;
        var head = lines[0].Trim();
        try
        {
            if (head.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase)) return Sequence(lines.Skip(1).ToList());
            if (Regex.IsMatch(head, @"^(graph|flowchart)\s*(TD|TB|LR|RL|BT)?\b", RegexOptions.IgnoreCase)) return Graph(head, lines.Skip(1).ToList());
        }
        catch (Exception)
        {
            return null;            // a drawing that cannot be made is better replaced by the source than half-drawn
        }
        return null;
    }

    // ── Sequence diagrams ────────────────────────────────────────────────

    private abstract record Ev;
    private sealed record Msg(string From, string To, string Text, bool Dashed, bool Open) : Ev;
    private sealed record Note(List<string> Over, string Side, string Text) : Ev;
    private sealed record FragStart(string Kind, string Label) : Ev;
    private sealed record FragElse(string Label) : Ev;
    private sealed record FragEnd : Ev;

    private static string? Sequence(List<string> lines)
    {
        var order = new List<string>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var events = new List<Ev>();
        void Part(string id) { if (!labels.ContainsKey(id)) { labels[id] = id; order.Add(id); } }

        var arrow = new Regex(@"^(?<a>[^\s\-]+?)\s*(?<op>--?>>|--?>|--?x|--?\))\s*[+\-]?(?<b>[^\s:]+?)\s*:\s*(?<t>.*)$");
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            var pm = Regex.Match(l, @"^(participant|actor)\s+(\S+)(?:\s+as\s+(.+))?$", RegexOptions.IgnoreCase);
            if (pm.Success)
            {
                var id = pm.Groups[2].Value; Part(id);
                if (pm.Groups[3].Success) labels[id] = pm.Groups[3].Value.Trim();
                continue;
            }
            var nm = Regex.Match(l, @"^Note\s+(over|right of|left of)\s+([^:]+):\s*(.*)$", RegexOptions.IgnoreCase);
            if (nm.Success)
            {
                var over = nm.Groups[2].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                foreach (var o in over) Part(o);
                events.Add(new Note(over, nm.Groups[1].Value.ToLowerInvariant(), nm.Groups[3].Value.Trim()));
                continue;
            }
            var fm = Regex.Match(l, @"^(loop|alt|opt|par|critical|break|rect)\b\s*(.*)$", RegexOptions.IgnoreCase);
            if (fm.Success) { events.Add(new FragStart(fm.Groups[1].Value.ToLowerInvariant(), fm.Groups[2].Value.Trim())); continue; }
            var em = Regex.Match(l, @"^(else|and|option)\b\s*(.*)$", RegexOptions.IgnoreCase);
            if (em.Success) { events.Add(new FragElse(em.Groups[2].Value.Trim())); continue; }
            if (string.Equals(l, "end", StringComparison.OrdinalIgnoreCase)) { events.Add(new FragEnd()); continue; }
            if (Regex.IsMatch(l, @"^(activate|deactivate|autonumber|title)\b", RegexOptions.IgnoreCase)) continue;
            var am = arrow.Match(l);
            if (am.Success)
            {
                Part(am.Groups["a"].Value); Part(am.Groups["b"].Value);
                var op = am.Groups["op"].Value;
                events.Add(new Msg(am.Groups["a"].Value, am.Groups["b"].Value, am.Groups["t"].Value.Trim(), op.StartsWith("--"), !op.EndsWith(">>")));
                continue;
            }
            // anything else (links, boxes, rect colours): not drawn, but not worth failing the diagram
        }
        if (order.Count < 2 || events.Count == 0) return null;

        var n = order.Count;
        var colW = n <= 4 ? 190 : n <= 7 ? 150 : n <= 10 ? 120 : 104;
        var left = 16;
        var width = left * 2 + n * colW;
        double X(string id) => left + order.IndexOf(id) * colW + colW / 2.0;
        var boxW = colW - 14;
        var charW = 5.7;

        var headerLines = order.Select(id => Wrap(labels[id], (int)(boxW / charW)).Count).Max();
        var headerH = 14 + headerLines * 14;
        var y = headerH + 22.0;
        var safeCuts = new List<double>();
        var body = new StringBuilder();
        var frames = new Stack<(string Kind, string Label, double Y0, int Depth)>();
        var frameShapes = new List<string>();
        var depth = 0;

        foreach (var ev in events)
        {
            switch (ev)
            {
                case FragStart f:
                    frames.Push((f.Kind, f.Label, y - 6, depth)); depth++; y += 16;
                    break;
                case FragElse e:
                    if (frames.Count > 0)
                    {
                        var inset = 8 + (frames.Peek().Depth) * 6;
                        body.Append($"<line x1=\"{inset}\" y1=\"{F(y - 6)}\" x2=\"{width - inset}\" y2=\"{F(y - 6)}\" stroke=\"#94a3b8\" stroke-dasharray=\"5 4\"/>");
                        if (e.Label.Length > 0) body.Append(Text(inset + 8, y + 6, $"[{e.Label}]", "start", 10, "#475569", italic: true));
                        y += 16;
                    }
                    break;
                case FragEnd:
                    if (frames.Count > 0)
                    {
                        var (kind, label, y0, d) = frames.Pop(); depth--;
                        var inset = 8 + d * 6;
                        frameShapes.Add($"<rect x=\"{inset}\" y=\"{F(y0)}\" width=\"{width - 2 * inset}\" height=\"{F(y - y0)}\" fill=\"none\" stroke=\"#64748b\" rx=\"3\"/>" +
                            $"<rect x=\"{inset}\" y=\"{F(y0)}\" width=\"{F(kind.Length * 6.4 + 14)}\" height=\"16\" fill=\"#e2e8f0\" stroke=\"#64748b\"/>" +
                            Text(inset + 6, y0 + 12, kind, "start", 10, Ink, bold: true) +
                            (label.Length > 0 ? Text(inset + kind.Length * 6.4 + 22, y0 + 12, $"[{label}]", "start", 10, "#475569", italic: true) : ""));
                        y += 8;
                    }
                    break;
                case Note nt:
                {
                    var xs = nt.Over.Select(X).ToList();
                    double x1, x2;
                    if (nt.Side == "over") { x1 = xs.Min() - 56; x2 = xs.Max() + 56; if (xs.Count == 1) { x1 = xs[0] - 66; x2 = xs[0] + 66; } }
                    else if (nt.Side == "right of") { x1 = xs[0] + 8; x2 = x1 + 150; }
                    else { x2 = xs[0] - 8; x1 = x2 - 150; }
                    x1 = Math.Max(4, x1); x2 = Math.Min(width - 4, x2);
                    var wrapped = Wrap(nt.Text, (int)((x2 - x1 - 12) / charW));
                    var h = wrapped.Count * 13 + 9;
                    body.Append($"<rect x=\"{F(x1)}\" y=\"{F(y)}\" width=\"{F(x2 - x1)}\" height=\"{F(h)}\" fill=\"#fef9c3\" stroke=\"#ca8a04\"/>");
                    for (var i = 0; i < wrapped.Count; i++) body.Append(Text((x1 + x2) / 2, y + 13 + i * 13, wrapped[i], "middle", 10, "#422006"));
                    y += h + 10;
                    safeCuts.Add(y - 4);
                    break;
                }
                case Msg m:
                {
                    var xa = X(m.From); var xb = X(m.To);
                    var self = m.From == m.To;
                    var span = self ? 150 : Math.Abs(xb - xa) - 8;
                    var wrapped = Wrap(m.Text, Math.Max(10, (int)(Math.Max(span, 60) / charW)));
                    var textH = wrapped.Count * 12;
                    var ty = y;
                    for (var i = 0; i < wrapped.Count; i++)
                        body.Append(Text(self ? xa + 12 : (xa + xb) / 2, ty + 10 + i * 12, wrapped[i], self ? "start" : "middle", 10, Ink));
                    var ay = y + textH + 8;
                    var dash = m.Dashed ? " stroke-dasharray=\"5 3\"" : "";
                    if (self)
                    {
                        body.Append($"<path d=\"M{F(xa)},{F(ay - 4)} h26 v14 h-26\" fill=\"none\" stroke=\"{Ink}\"{dash} marker-end=\"url(#{(m.Open ? "open" : "head")})\"/>");
                        y = ay + 22;
                    }
                    else
                    {
                        body.Append($"<line x1=\"{F(xa)}\" y1=\"{F(ay)}\" x2=\"{F(xb)}\" y2=\"{F(ay)}\" stroke=\"{Ink}\"{dash} marker-end=\"url(#{(m.Open ? "open" : "head")})\"/>");
                        y = ay + 16;
                    }
                    safeCuts.Add(y - 6);
                    break;
                }
            }
        }
        while (frames.Count > 0) { var f = frames.Pop(); frameShapes.Add($"<rect x=\"8\" y=\"{F(f.Y0)}\" width=\"{width - 16}\" height=\"{F(y - f.Y0)}\" fill=\"none\" stroke=\"#64748b\"/>"); }
        var height = y + 14;

        var lifelines = new StringBuilder();
        foreach (var id in order)
            lifelines.Append($"<line x1=\"{F(X(id))}\" y1=\"{headerH}\" x2=\"{F(X(id))}\" y2=\"{F(height - 6)}\" stroke=\"#cbd5e1\" stroke-dasharray=\"2 4\"/>");
        var frameMarkup = string.Concat(frameShapes.AsEnumerable().Reverse());
        var header = new StringBuilder();
        foreach (var id in order)
        {
            var wl = Wrap(labels[id], (int)(boxW / charW));
            header.Append($"<rect x=\"{F(X(id) - boxW / 2.0)}\" y=\"4\" width=\"{boxW}\" height=\"{headerH - 6}\" rx=\"4\" fill=\"#e0f2fe\" stroke=\"#0369a1\"/>");
            for (var i = 0; i < wl.Count; i++) header.Append(Text(X(id), 20 + i * 14, wl[i], "middle", 11, "#0c4a6e", bold: true));
        }
        var content = lifelines.ToString() + frameMarkup + body;

        // A wide diagram goes on a landscape page, and one taller than a page is cut between messages into
        // several pages that each repeat the participant header, so the text stays readable.
        var wide = width > 900;
        var pageH = wide ? width / 1.55 : width * 1.4;
        var cls = wide ? "diagram wide" : "diagram";
        var hdr = headerH + 8;
        string Page(double viewY, double viewH, bool repeatHeader) =>
            $"<figure class=\"{cls}\"><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 {F(viewY)} {width} {F(viewH)}\" width=\"100%\" {Font} role=\"img\">" +
            Defs() + content +
            (repeatHeader ? $"<rect x=\"0\" y=\"{F(viewY)}\" width=\"{width}\" height=\"{hdr}\" fill=\"#ffffff\"/><g transform=\"translate(0,{F(viewY)})\">{header}</g>" : header.ToString()) +
            "</svg></figure>";

        if (height <= pageH) return Page(0, height, repeatHeader: false);
        var figures = new StringBuilder();
        double from = 0;                                     // y where this page's real content starts
        var first = true;
        while (from < height - 1)
        {
            var room = first ? pageH : pageH - hdr;
            var limit = from + room;
            var cut = safeCuts.Where(c => c > from + 20 && c <= limit).DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(cut) || limit >= height) cut = limit >= height ? height : Math.Min(limit, height);
            if (first) figures.Append(Page(0, cut, repeatHeader: false));
            else figures.Append(Page(from - hdr, cut - from + hdr, repeatHeader: true));
            from = cut; first = false;
        }
        return figures.ToString();
    }

    // ── Flow / dependency graphs ─────────────────────────────────────────

    private sealed record Node(string Id, string Label, string Shape);

    private static string? Graph(string head, List<string> lines)
    {
        var dir = Regex.Match(head, @"\b(TD|TB|LR|RL|BT)\b", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        var horizontal = dir is "LR" or "RL";
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var order = new List<string>();
        var edges = new List<(string From, string To, string Label, bool Dotted)>();

        Node Ensure(string id, string? label = null, string shape = "rect")
        {
            if (!nodes.TryGetValue(id, out var n)) { n = new Node(id, label ?? id, shape); nodes[id] = n; order.Add(id); }
            else if (label is not null && n.Label == n.Id) { n = n with { Label = label, Shape = shape }; nodes[id] = n; }
            return n;
        }

        string Parse(string token)
        {
            token = token.Trim().TrimEnd(';');
            var m = Regex.Match(token, @"^([A-Za-z0-9_\.\-:]+)\s*(?:\[\[?""?(.*?)""?\]?\]|\(\(?""?(.*?)""?\)?\)|\{""?(.*?)""?\}|>""?(.*?)""?\])?$");
            if (!m.Success) return Ensure(Regex.Replace(token, @"\W", "_"), token).Id;
            var id = m.Groups[1].Value;
            var label = new[] { 2, 3, 4, 5 }.Select(i => m.Groups[i]).FirstOrDefault(g => g.Success)?.Value;
            var shape = m.Groups[4].Success ? "diamond" : m.Groups[3].Success ? "round" : "rect";
            Ensure(id, label is null ? null : label.Replace("<br/>", " ").Replace("<br>", " "), shape);
            return id;
        }

        // The label group always takes part (empty when absent), so Split yields [node, op, label, node ...] for every edge.
        var edgeSplit = new Regex(@"\s*(-->|---|-\.->|==>|--\s?>|-\.-)\s*((?:\|[^|]*\|)?)\s*");
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase) || l.Equals("end", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("classDef", StringComparison.Ordinal) || l.StartsWith("class ", StringComparison.Ordinal) || l.StartsWith("style ", StringComparison.Ordinal)
                || l.StartsWith("click ", StringComparison.Ordinal) || l.StartsWith("direction", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = edgeSplit.Split(l);                      // [node, op, label, node, op, label, node …]
            if (parts.Length == 1) { Parse(l); continue; }
            var prev = Parse(parts[0]);
            for (var i = 1; i + 2 < parts.Length; i += 3)
            {
                var op = parts[i]; var label = parts[i + 1].Trim('|'); var next = Parse(parts[i + 2]);
                edges.Add((prev, next, label.Trim(), op.Contains('.')));
                prev = next;
            }
        }
        if (order.Count == 0) return null;

        // Rank = longest path from a source (cycle-safe).
        var rank = order.ToDictionary(i => i, _ => 0);
        for (var pass = 0; pass < order.Count; pass++)
        {
            var changed = false;
            foreach (var e in edges.Where(e => e.From != e.To))
                if (rank[e.To] < rank[e.From] + 1 && rank[e.From] + 1 <= order.Count) { rank[e.To] = rank[e.From] + 1; changed = true; }
            if (!changed) break;
        }
        const int perRow = 6;
        var connected = new HashSet<string>(edges.Where(e => e.From != e.To).SelectMany(e => new[] { e.From, e.To }));
        var nw = order.ToDictionary(i => i, i => Math.Max(96, (int)(nodes[i].Label.Length * 6.3 + 24)));
        var pos = new Dictionary<string, (double X, double Y, int W)>();
        double yy = 24; double maxW = 0;
        foreach (var r in rank.Values.Distinct().OrderBy(v => v))
        {
            var ids = order.Where(i => rank[i] == r).ToList();
            for (var s = 0; s < ids.Count; s += perRow)
            {
                var rowIds = ids.Skip(s).Take(perRow).ToList();
                var rowW = rowIds.Sum(i => nw[i]) + (rowIds.Count - 1) * 36;
                maxW = Math.Max(maxW, rowW);
            }
        }
        var width = Math.Max(240, maxW + 48);
        foreach (var r in rank.Values.Distinct().OrderBy(v => v))
        {
            var ids = order.Where(i => rank[i] == r).OrderBy(i => connected.Contains(i) ? 1 : 0).ToList();
            for (var s = 0; s < ids.Count; s += perRow)
            {
                var rowIds = ids.Skip(s).Take(perRow).ToList();
                var rowW = rowIds.Sum(i => nw[i]) + (rowIds.Count - 1) * 36;
                var x = (width - rowW) / 2;
                foreach (var id in rowIds) { pos[id] = (x, yy, nw[id]); x += nw[id] + 36; }
                yy += 52 + (edges.Any(e => e.Label.Length > 0) ? 12 : 0);
            }
            yy += 14;
        }
        var height = yy + 6;

        var sb = new StringBuilder();
        sb.Append($"<figure class=\"diagram\"><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(width)} {F(height)}\" width=\"100%\" style=\"max-width:{F(width * 1.4)}px;margin:0 auto\" {Font} role=\"img\">");
        sb.Append(Defs());
        foreach (var e in edges)
        {
            if (!pos.TryGetValue(e.From, out var a) || !pos.TryGetValue(e.To, out var b)) continue;
            var dash = e.Dotted ? " stroke-dasharray=\"4 3\"" : "";
            if (e.From == e.To)
            {
                sb.Append($"<path d=\"M{F(a.X + a.W)},{F(a.Y + 12)} c28,-4 28,28 0,24\" fill=\"none\" stroke=\"{Ink}\"{dash} marker-end=\"url(#head)\"/>");
                continue;
            }
            double x1 = a.X + a.W / 2.0, y1 = a.Y + 32, x2 = b.X + b.W / 2.0, y2 = b.Y;
            if (b.Y <= a.Y) { y1 = a.Y + 32; y2 = b.Y + 32; var my = a.Y + 46; sb.Append($"<path d=\"M{F(x1)},{F(y1)} C{F(x1)},{F(my)} {F(x2)},{F(my)} {F(x2)},{F(y2)}\" fill=\"none\" stroke=\"{Ink}\"{dash} marker-end=\"url(#head)\"/>"); }
            else { var my = (y1 + y2) / 2; sb.Append($"<path d=\"M{F(x1)},{F(y1)} C{F(x1)},{F(my)} {F(x2)},{F(my)} {F(x2)},{F(y2)}\" fill=\"none\" stroke=\"{Ink}\"{dash} marker-end=\"url(#head)\"/>"); }
            if (e.Label.Length > 0) sb.Append(Text((x1 + x2) / 2, (y1 + y2) / 2 - 3, e.Label, "middle", 10, "#475569"));
        }
        foreach (var id in order)
        {
            var p = pos[id]; var n = nodes[id];
            var fill = n.Shape == "diamond" ? "#fef3c7" : "#e0f2fe"; var stroke = n.Shape == "diamond" ? "#b45309" : "#0369a1";
            sb.Append($"<rect x=\"{F(p.X)}\" y=\"{F(p.Y)}\" width=\"{p.W}\" height=\"32\" rx=\"{(n.Shape == "round" ? 16 : 5)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
            sb.Append(Text(p.X + p.W / 2.0, p.Y + 20, n.Label, "middle", 11, "#0c4a6e", bold: true));
        }
        sb.Append("</svg></figure>");
        _ = horizontal;
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string Defs() =>
        "<defs>" +
        $"<marker id=\"head\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"{Ink}\"/></marker>" +
        $"<marker id=\"open\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10\" fill=\"none\" stroke=\"{Ink}\"/></marker>" +
        "</defs>";

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Text(double x, double y, string text, string anchor, int size, string color, bool bold = false, bool italic = false) =>
        $"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" font-size=\"{size}\" fill=\"{color}\"" +
        (bold ? " font-weight=\"700\"" : "") + (italic ? " font-style=\"italic\"" : "") + $">{Esc(text)}</text>";

    /// <summary>Greedy word wrap on a character budget; a word longer than the budget is cut so it can never overflow its box.</summary>
    private static List<string> Wrap(string text, int max)
    {
        max = Math.Max(6, max);
        var result = new List<string>();
        var cur = new StringBuilder();
        foreach (var rawWord in text.Replace("<br/>", " ").Replace("<br>", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = rawWord;
            while (word.Length > max) { if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); } result.Add(word[..max]); word = word[max..]; }
            if (cur.Length + word.Length + (cur.Length > 0 ? 1 : 0) > max) { result.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(word);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        if (result.Count == 0) result.Add("");
        return result;
    }
}
