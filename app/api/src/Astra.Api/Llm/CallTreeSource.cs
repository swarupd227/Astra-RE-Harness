using System.Text;

namespace Astra.Api.Llm;

/// <summary>
/// The source of a routine and of the routines it calls, as a read-only reference for package generation. A signed spec
/// says *what* a routine does; when it sends a request to an outside service, the exact keys and nesting live in code —
/// often in a helper it calls (<c>CalculateApiPremium</c> → <c>BuildRequestJson</c>) — and a package written from the
/// spec alone guessed them and was refused by the real service. Pure: the caller supplies the routines and file text.
/// </summary>
public static class CallTreeSource
{
    public sealed record Routine(string Name, string Path, int LineStart, int LineEnd, IReadOnlyList<string> Calls);

    /// <summary>Most text the note may hold; a routine that would pass it is named but not quoted.</summary>
    public const int MaxChars = 40_000;

    /// <summary>
    /// The root routine and every in-project routine it reaches (breadth first, up to <paramref name="maxDepth"/> calls
    /// away), each quoted from its file by its own lines. Returns null when there is nothing beyond the root to show and
    /// no root text either. Names resolve case-insensitively; a call that names no routine of the project is skipped.
    /// </summary>
    public static string? Describe(Routine root, IReadOnlyList<Routine> project, Func<string, string?> fileText, int maxDepth = 2)
    {
        var byName = project.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var order = new List<(Routine R, int Depth)> { (root, 0) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name };
        for (var i = 0; i < order.Count; i++)
        {
            var (r, depth) = order[i];
            if (depth >= maxDepth) continue;
            foreach (var call in r.Calls)
                if (byName.TryGetValue(call, out var callee) && seen.Add(callee.Name))
                    order.Add((callee, depth + 1));
        }
        if (order.Count == 1) return null; // a routine that calls nothing: the spec and the original source already cover it

        var lines = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("Read-only reference, not something to convert file by file. Where this routine (or one it calls) builds a request for an");
        sb.AppendLine("outside service, reproduce that request key for key from this code — names, nesting, order and value types — and take");
        sb.AppendLine("default inputs from it. The signed spec remains the contract for behaviour.");
        var left = new List<string>();
        foreach (var (r, depth) in order)
        {
            if (!lines.TryGetValue(r.Path, out var fileLines))
            {
                var text = fileText(r.Path);
                fileLines = text is null ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');
                lines[r.Path] = fileLines;
            }
            var start = Math.Max(1, r.LineStart);
            var end = Math.Min(fileLines.Length, Math.Max(start, r.LineEnd));
            if (fileLines.Length == 0 || start > fileLines.Length) { left.Add(r.Name); continue; }
            var body = string.Join("\n", fileLines[(start - 1)..end]);
            var block = $"\n### {r.Name}{(depth == 0 ? " (this routine)" : $" (called, depth {depth})")} — {ShortPath(r.Path)} L{start}–L{end}\n\n```\n{body}\n```\n";
            if (sb.Length + block.Length > MaxChars) { left.Add(r.Name); continue; }
            sb.Append(block);
        }
        if (left.Count > 0)
            sb.Append("\nAlso called, not quoted here (too long or unreadable): ").Append(string.Join(", ", left)).AppendLine(".");
        return sb.ToString().TrimEnd();
    }

    private static string ShortPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
