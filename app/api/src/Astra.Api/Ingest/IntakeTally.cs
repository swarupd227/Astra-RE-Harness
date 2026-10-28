namespace Astra.Api.Ingest;

/// <summary>
/// Records what an intake step (expanding a zip, walking a cloned repository) leaves out, so the
/// ingest result can say so instead of dropping files without a word. It reports a summary, not
/// one line per file: a repository holds thousands of images and docs that were never going to be
/// source, so those are counted by extension, while the few files that were refused for a reason
/// worth acting on (too large, unsafe path) are named.
/// </summary>
public sealed class IntakeTally
{
    public const string NotSource = "not-source";
    public const string TooLarge = "too-large";
    public const string Unsafe = "unsafe-path";

    private const int MaxNamed = 5;
    private const int MaxExtensions = 5;

    private readonly Dictionary<string, List<string>> _byReason = new(StringComparer.Ordinal);

    public int Total => _byReason.Values.Sum(l => l.Count);

    public void Skip(string reason, string path)
    {
        if (!_byReason.TryGetValue(reason, out var list)) _byReason[reason] = list = new List<string>();
        list.Add(path);
    }

    /// <summary>One sentence per reason that occurred, most routine first. Empty when nothing was skipped.</summary>
    public IReadOnlyList<string> Notes(long maxFileBytes = 8L * 1024 * 1024)
    {
        var notes = new List<string>();

        if (_byReason.TryGetValue(NotSource, out var notSource) && notSource.Count > 0)
        {
            var byExt = notSource
                .GroupBy(p =>
                {
                    var e = Path.GetExtension(p);
                    return e.Length == 0 ? "no extension" : e.ToLowerInvariant();
                })
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            var shown = string.Join(", ", byExt.Take(MaxExtensions).Select(g => $"{g.Key} ×{g.Count()}"));
            var more = byExt.Count > MaxExtensions ? ", …" : "";
            notes.Add($"Skipped {Files(notSource.Count)} that {(notSource.Count == 1 ? "is" : "are")} not supported source code ({shown}{more}).");
        }

        if (_byReason.TryGetValue(TooLarge, out var tooLarge) && tooLarge.Count > 0)
            notes.Add($"Skipped {Files(tooLarge.Count)} over {maxFileBytes / (1024 * 1024)} MiB: {Named(tooLarge)}.");

        if (_byReason.TryGetValue(Unsafe, out var unsafePaths) && unsafePaths.Count > 0)
            notes.Add($"Skipped {Files(unsafePaths.Count)} with an unsafe path (absolute, or containing \"..\"): {Named(unsafePaths)}.");

        return notes;
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n:N0} files";

    private static string Named(List<string> paths)
    {
        var shown = string.Join(", ", paths.Take(MaxNamed));
        return paths.Count > MaxNamed ? $"{shown} (+{paths.Count - MaxNamed} more)" : shown;
    }
}
