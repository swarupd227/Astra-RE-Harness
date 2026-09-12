using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Docs;

/// <summary>
/// Small, dependency-free markdown helpers the writers and the critic share:
/// heading inspection, title enforcement, empty-section and table-length
/// detection, code stripping. Nothing here assembles content — the model
/// writes the document; this only validates and frames it.
/// </summary>
public static class DocMarkdown
{
    public sealed record Heading(int Level, string Text, int Line);

    public static string Normalize(string md)
    {
        var s = (md ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return s.Length == 0 ? "" : s + "\n";
    }

    /// <summary>ATX heading depth (1–6) when the line is a heading, else 0.</summary>
    public static int HeadingDepth(string line)
    {
        var h = 0;
        while (h < line.Length && line[h] == '#') h++;
        return h is >= 1 and <= 6 && h < line.Length && line[h] == ' ' ? h : 0;
    }

    public static IReadOnlyList<Heading> Headings(string md)
    {
        var lines = Normalize(md).Split('\n');
        var list = new List<Heading>();
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            var depth = HeadingDepth(lines[i]);
            if (depth > 0)
                list.Add(new Heading(depth, lines[i][depth..].Trim().TrimEnd('#').Trim(), i));
        }
        return list;
    }

    /// <summary>
    /// Make sure the document opens with a heading at <paramref name="level"/>.
    /// If the first content line is already a heading with the same text it is
    /// re-levelled; a heading with different text at the same or a shallower
    /// level is accepted as the title; otherwise the title is prepended.
    /// </summary>
    public static string EnsureTitle(string md, string title, int level = 1)
    {
        md = Normalize(md);
        title = title.Trim();
        var hashes = new string('#', level);
        if (md.Length == 0) return hashes + " " + title + "\n";

        var lines = md.Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first >= 0)
        {
            var depth = HeadingDepth(lines[first]);
            if (depth > 0)
            {
                var text = lines[first][depth..].Trim().TrimEnd('#').Trim();
                if (Similar(text, title))
                {
                    lines[first] = hashes + " " + text;
                    return string.Join('\n', lines);
                }
                if (depth <= level) return md;
            }
        }
        return hashes + " " + title + "\n\n" + md;
    }

    /// <summary>Shift every heading so the shallowest becomes
    /// <paramref name="targetTopLevel"/>, preserving relative depth. Fenced
    /// code is left alone.</summary>
    public static string ShiftHeadings(string md, int targetTopLevel)
    {
        var headings = Headings(md);
        if (headings.Count == 0) return Normalize(md);
        var delta = targetTopLevel - headings.Min(h => h.Level);
        if (delta == 0) return Normalize(md);

        var lines = Normalize(md).Split('\n');
        var inFence = false;
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                sb.Append(line).Append('\n');
                continue;
            }
            var depth = inFence ? 0 : HeadingDepth(line);
            if (depth > 0)
                sb.Append(new string('#', Math.Clamp(depth + delta, 1, 6))).Append(line[depth..]).Append('\n');
            else
                sb.Append(line).Append('\n');
        }
        return Normalize(sb.ToString());
    }

    /// <summary>Headings whose section (up to the next heading of the same or
    /// a shallower level) contains no non-blank, non-heading line.</summary>
    public static IReadOnlyList<string> EmptySections(string md)
    {
        var lines = Normalize(md).Split('\n');
        var headings = Headings(md);
        var empty = new List<string>();
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            var end = lines.Length;
            for (var j = i + 1; j < headings.Count; j++)
            {
                if (headings[j].Level <= h.Level) { end = headings[j].Line; break; }
            }
            var hasContent = false;
            for (var k = h.Line + 1; k < end; k++)
            {
                var line = lines[k];
                if (line.Trim().Length == 0) continue;
                if (HeadingDepth(line) > 0) continue;
                hasContent = true;
                break;
            }
            if (!hasContent) empty.Add(h.Text);
        }
        return empty;
    }

    /// <summary>Longest markdown table in the document, in body rows
    /// (header and separator excluded). 0 when there is no table.</summary>
    public static int MaxTableRows(string md)
    {
        var lines = Normalize(md).Split('\n');
        var best = 0;
        var run = 0;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith('|'))
            {
                run++;
            }
            else
            {
                best = Math.Max(best, run);
                run = 0;
            }
        }
        best = Math.Max(best, run);
        return Math.Max(0, best - 2);
    }

    /// <summary>Remove fenced code blocks and inline code spans so prose
    /// checks do not trip over identifiers.</summary>
    public static string StripCode(string md)
    {
        var withoutFences = Regex.Replace(Normalize(md), @"```[\s\S]*?```|~~~[\s\S]*?~~~", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(withoutFences, @"`[^`\n]*`", " ", RegexOptions.CultureInvariant);
    }

    /// <summary>Whole-identifier, case-insensitive mention check. Handles
    /// dotted names ("ProductDao.findProgramById") and names inside backticks.</summary>
    public static bool MentionsRoutine(string md, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var pattern = @"(?<![A-Za-z0-9_])" + Regex.Escape(name.Trim()) + @"(?![A-Za-z0-9_])";
        return Regex.IsMatch(md, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string AppendSection(string md, string heading, string body) =>
        Normalize(Normalize(md) + "\n" + heading.Trim() + "\n\n" + body.Trim() + "\n");

    public static string FirstParagraph(string md)
    {
        foreach (var block in Normalize(md).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var t = block.Trim();
            if (t.Length == 0 || HeadingDepth(t) > 0 || t.StartsWith('|') || t.StartsWith("```", StringComparison.Ordinal)) continue;
            return t.Replace('\n', ' ');
        }
        return "";
    }

    private static bool Similar(string a, string b)
    {
        static string Fold(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "");
        var x = Fold(a);
        var y = Fold(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }
}
