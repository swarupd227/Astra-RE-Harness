using System.Text.RegularExpressions;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// The documentation citation format <c>[path:L&lt;start&gt;–L&lt;end&gt;]</c>
/// (hyphen or dash, optional second <c>L</c>, single line allowed) and its
/// resolution against the corpus: a citation is valid only when the path is
/// a real source file of the version and the line range lies inside one of
/// that file's parsed routines. This is what makes "no coarse pointers"
/// enforceable rather than aspirational.
/// </summary>
public static class DocCitations
{
    public static readonly Regex Pattern = new(
        @"\[(?<path>[^\[\]:\n]+?):L(?<start>\d+)(?:\s*[–\-—]\s*L?(?<end>\d+))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record Citation(string Path, int Start, int End, string Raw);

    public static IReadOnlyList<Citation> Parse(string markdown)
    {
        var list = new List<Citation>();
        if (string.IsNullOrEmpty(markdown)) return list;
        foreach (Match m in Pattern.Matches(markdown))
        {
            var start = int.Parse(m.Groups["start"].Value);
            var end = m.Groups["end"].Success ? int.Parse(m.Groups["end"].Value) : start;
            list.Add(new Citation(m.Groups["path"].Value.Trim(), start, end, m.Value));
        }
        return list;
    }

    public static string Format(string path, int start, int end) =>
        end > start ? $"[{path}:L{start}–L{end}]" : $"[{path}:L{start}]";

    public sealed record RoutineSpan(string Path, string Name, int LineStart, int LineEnd);

    public sealed record Resolution(bool Ok, string? Problem, RoutineSpan? Routine);

    public sealed record CheckResult(int Total, IReadOnlyList<string> Problems);

    /// <summary>Every citation in the markdown resolved against the index.
    /// Duplicate citations are reported once.</summary>
    public static CheckResult Check(string markdown, CorpusIndex index)
    {
        var cites = Parse(markdown);
        var problems = new List<string>();
        foreach (var c in cites.DistinctBy(x => x.Raw))
        {
            var r = index.Resolve(c);
            if (!r.Ok) problems.Add($"{c.Raw}: {r.Problem}");
        }
        return new CheckResult(cites.Count, problems);
    }

    /// <summary>
    /// Path → routine spans for one source version. Paths are matched
    /// case-insensitively with slashes normalised; a citation may use the full
    /// relative path, a unique trailing sub-path, or a unique file name.
    /// </summary>
    public sealed class CorpusIndex
    {
        private readonly Dictionary<string, List<RoutineSpan>> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _byBasename = new(StringComparer.OrdinalIgnoreCase);

        public static CorpusIndex Empty { get; } = new(Array.Empty<RoutineSpan>());

        public CorpusIndex(IEnumerable<RoutineSpan> spans)
        {
            foreach (var s in spans)
            {
                var path = NormalizePath(s.Path);
                if (path.Length == 0) continue;
                if (!_byPath.TryGetValue(path, out var list))
                {
                    _byPath[path] = list = new List<RoutineSpan>();
                    var slash = path.LastIndexOf('/');
                    var basename = slash >= 0 ? path[(slash + 1)..] : path;
                    if (!_byBasename.TryGetValue(basename, out var paths))
                        _byBasename[basename] = paths = new List<string>();
                    paths.Add(path);
                }
                list.Add(s with { Path = path });
            }
        }

        public int FileCount => _byPath.Count;
        public int RoutineCount => _byPath.Values.Sum(l => l.Count);

        public static async Task<CorpusIndex> LoadAsync(AppDbContext db, Guid sourceVersionId, CancellationToken ct)
        {
            var rows = await db.Subroutines
                .AsNoTracking()
                .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
                .Select(s => new { Path = s.SourceFile!.RelativePath, s.Name, s.LineStart, s.LineEnd })
                .ToListAsync(ct);
            return new CorpusIndex(rows.Select(r => new RoutineSpan(r.Path, r.Name, r.LineStart, r.LineEnd)));
        }

        /// <summary>The indexed path a cited path refers to, or null.</summary>
        public string? ResolvePath(string cited)
        {
            var p = NormalizePath(cited);
            if (p.Length == 0) return null;
            if (_byPath.ContainsKey(p)) return _byPath.Keys.First(k => string.Equals(k, p, StringComparison.OrdinalIgnoreCase));

            // Unique trailing sub-path ("blas/dgemm.f" for "src/blas/dgemm.f").
            var suffix = _byPath.Keys.Where(k => k.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase)).ToList();
            if (suffix.Count == 1) return suffix[0];
            if (suffix.Count > 1) return null;

            // Unique file name.
            var slash = p.LastIndexOf('/');
            var basename = slash >= 0 ? p[(slash + 1)..] : p;
            if (_byBasename.TryGetValue(basename, out var paths) && paths.Count == 1) return paths[0];
            return null;
        }

        public Resolution Resolve(Citation c)
        {
            if (c.Start < 1 || c.End < c.Start)
                return new Resolution(false, $"invalid line range L{c.Start}–L{c.End}", null);
            var path = ResolvePath(c.Path);
            if (path is null)
                return new Resolution(false, $"unknown path '{c.Path}'", null);
            var hit = _byPath[path].FirstOrDefault(r => r.LineStart <= c.Start && c.End <= r.LineEnd);
            return hit is null
                ? new Resolution(false, $"lines {c.Start}–{c.End} of '{path}' fall outside every routine", null)
                : new Resolution(true, null, hit);
        }

        public IReadOnlyList<RoutineSpan> RoutinesIn(string path) =>
            ResolvePath(path) is { } p ? _byPath[p] : Array.Empty<RoutineSpan>();

        private static string NormalizePath(string p)
        {
            var s = (p ?? "").Trim().Replace('\\', '/');
            while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
            return s.TrimStart('/');
        }
    }
}
