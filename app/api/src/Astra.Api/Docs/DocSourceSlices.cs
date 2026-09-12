using System.Text;

namespace Astra.Api.Docs;

/// <summary>Line-numbered source slices for writer prompts, capped per
/// routine with an explicit note rather than a silent cut.</summary>
public static class DocSourceSlices
{
    public static string[] SplitLines(string source) =>
        (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Lines <paramref name="lineStart"/>..<paramref name="lineEnd"/> (1-based,
    /// inclusive) as <c>NNN: text</c>, at most <paramref name="cap"/> lines.
    /// When cut, the last line states how much was omitted so the reader —
    /// model or human — never mistakes a slice for the whole routine.
    /// </summary>
    public static string Slice(string[] lines, int lineStart, int lineEnd, int cap)
    {
        if (lines.Length == 0) return "";
        var lo = lineStart <= 0 ? 0 : Math.Max(0, lineStart - 1);
        var hi = lineEnd <= 0 || lineEnd < lineStart ? lines.Length : Math.Min(lines.Length, lineEnd);
        if (hi <= lo) return "";

        var total = hi - lo;
        var truncated = cap > 0 && total > cap;
        var stop = truncated ? lo + cap : hi;

        var sb = new StringBuilder();
        for (var i = lo; i < stop; i++)
            sb.Append(i + 1).Append(": ").Append(lines[i]).Append('\n');
        if (truncated)
            sb.Append("… source truncated: showing lines ").Append(lo + 1).Append('–').Append(stop)
              .Append(" of ").Append(lo + 1).Append('–').Append(hi)
              .Append(" (").Append(total - cap).Append(" lines omitted); the summary covers the rest\n");
        return sb.ToString();
    }
}
