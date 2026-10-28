using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>
/// The citation handle for one piece of evidence: a short id such as <c>SQL-3fa9c01e</c>.
/// The prefix says what kind of evidence it is; the eight hex digits are a hash of the
/// file path and the locator inside it, so the same piece of evidence gets the same id in
/// every environment and on every rebuild (recorded model runs keep citing valid ids).
/// </summary>
public static class EvidenceIds
{
    private static readonly Regex Shape = new("^[A-Z]{3}-[0-9a-f]{8}(-[0-9]{1,3})?$", RegexOptions.Compiled);

    public static string Prefix(string kind) => kind switch
    {
        "code" => "SRC",
        "sql" => "SQL",
        "job" => "JOB",
        "etl" => "ETL",
        "config" => "CFG",
        "transcript" => "TRN",
        "ticket" => "TKT",
        "document" => "DOC",
        "audio" => "AUD",
        _ => "EVD",
    };

    /// <summary>The file-and-locator identity a short id is derived from.</summary>
    public static string KeyOf(string path, string locator) => path + "#" + locator;

    public static string Hash8(string path, string locator)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(KeyOf(path, locator)));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    public static string Base(string kind, string path, string locator) => $"{Prefix(kind)}-{Hash8(path, locator)}";

    /// <summary>
    /// Gives every item its short id. Two different pieces of evidence can, rarely, hash to the
    /// same eight digits; the one that sorts first by key keeps the plain id and the others get
    /// -2, -3 … in key order, so a full rebuild is deterministic. <paramref name="taken"/> holds
    /// ids already in use in the same version (id → key): an item that already owns its id keeps
    /// it, and one that would collide with someone else's id takes the next free suffix.
    /// </summary>
    public static IReadOnlyList<string> Assign(
        IReadOnlyList<(string Kind, string Path, string Locator)> items,
        IReadOnlyDictionary<string, string>? taken = null)
    {
        var result = new string[items.Count];
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        if (taken is not null)
            foreach (var (id, key) in taken) owner[id] = key;

        var order = Enumerable.Range(0, items.Count)
            .OrderBy(i => KeyOf(items[i].Path, items[i].Locator), StringComparer.Ordinal)
            .ThenBy(i => i)
            .ToList();

        foreach (var i in order)
        {
            var (kind, path, locator) = items[i];
            var key = KeyOf(path, locator);
            var baseId = Base(kind, path, locator);
            var candidate = baseId;
            var suffix = 1;
            while (owner.TryGetValue(candidate, out var heldBy) && heldBy != key)
            {
                suffix++;
                candidate = $"{baseId}-{suffix}";
            }
            owner[candidate] = key;
            result[i] = candidate;
        }
        return result;
    }

    /// <summary>
    /// Accepts what a person or a model might write around an id — brackets, spaces, a lower-case
    /// prefix — and returns the canonical form, or null when it is not shaped like an id.
    /// </summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('[', ']', '(', ')', '`', '"', '\'').Trim();
        var dash = s.IndexOf('-');
        if (dash != 3) return null;
        var candidate = s[..3].ToUpperInvariant() + "-" + s[4..].ToLowerInvariant();
        return Shape.IsMatch(candidate) ? candidate : null;
    }

    public static bool IsShortId(string? s) => s is not null && Shape.IsMatch(s);
}
