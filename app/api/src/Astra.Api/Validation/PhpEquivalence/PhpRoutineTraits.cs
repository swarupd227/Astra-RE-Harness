using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>
/// What a PHP routine needs from the world, read from its source text and its signed spec: a database, superglobals,
/// uploaded files, the network. Decides whether an equivalence run can supply it — a routine that reads or writes a
/// database is skipped (the PHP run has none; the HTTP parity replay is where it is checked), one that reads
/// $_GET/$_POST is run only when the vectors carry request parameters. Pure: no I/O.
/// </summary>
public static partial class PhpRoutineTraits
{
    public sealed record Traits(
        bool TouchesDatabase,
        string? DatabaseEvidence,
        IReadOnlyList<string> Superglobals,
        IReadOnlyList<string> Unsuppliable)
    {
        public bool ReadsRequest => Superglobals.Any(s => s is "_GET" or "_POST" or "_REQUEST" or "_COOKIE");
    }

    [GeneratedRegex(@"\b(mysqli?_(?:query|connect|prepare|real_query|multi_query|fetch_\w+|select_db|stmt_\w+)|pg_(?:query|connect|prepare|execute|fetch_\w+)|sqlite_\w+|oci_\w+|sqlsrv_\w+|odbc_\w+)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DbFunction();

    [GeneratedRegex(@"new\s+\\?(PDO|mysqli|SQLite3)\b|\$wpdb\b|\bDB::\w+\s*\(|->(?:query|prepare|exec|get_results|get_row|get_var|get_col|insert_id)\s*\(|\b(?:Capsule|Eloquent)::|::(?:query|where|find|findOrFail|create|firstOrCreate|updateOrCreate)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DbObject();

    [GeneratedRegex(@"\$(_GET|_POST|_REQUEST|_COOKIE|_SESSION|_SERVER|_FILES|_ENV|GLOBALS)\b")]
    private static partial Regex Superglobal();

    [GeneratedRegex(@"\b(curl_\w+|fsockopen|stream_socket_client|socket_\w+|mail|exec|shell_exec|system|passthru|proc_open|popen)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex OutsideWorld();

    [GeneratedRegex(@"\b(?:file_get_contents|fopen|readfile|file)\s*\(\s*['""]https?://", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteFile();

    public static Traits Analyze(string routineSource, JsonElement specRoot)
    {
        var code = StripComments(routineSource ?? "");
        string? dbEvidence = null;
        if (specRoot.ValueKind == JsonValueKind.Object
            && specRoot.TryGetProperty("data_accesses", out var da) && da.ValueKind == JsonValueKind.Array && da.GetArrayLength() > 0)
        {
            var first = da[0];
            var id = first.TryGetProperty("id", out var i) ? i.GetString() : "DA";
            var what = first.TryGetProperty("description", out var d) ? d.GetString() : first.TryGetProperty("operation", out var o) ? o.GetString() : null;
            dbEvidence = $"{id}: {Shorten(what ?? "a database statement", 120)}";
        }
        if (dbEvidence is null)
        {
            var m = DbFunction().Match(code);
            if (!m.Success) m = DbObject().Match(code);
            if (m.Success) dbEvidence = $"the code calls `{m.Value.Trim().TrimEnd('(').Trim()}`";
        }

        var sgs = Superglobal().Matches(code).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

        var unsuppliable = new List<string>();
        if (sgs.Contains("_FILES")) unsuppliable.Add("reads uploaded files ($_FILES), which an equivalence run cannot supply");
        var ow = OutsideWorld().Match(code);
        if (ow.Success) unsuppliable.Add($"calls `{ow.Groups[1].Value}()`, which the PHP sandbox does not allow (no network, no other programs)");
        else if (RemoteFile().IsMatch(code)) unsuppliable.Add("reads a remote URL, which the PHP sandbox does not allow");

        return new Traits(dbEvidence is not null, dbEvidence, sgs, unsuppliable);
    }

    /// <summary>The routine's lines out of its file (1-based, inclusive); the whole file when the range is unknown.</summary>
    public static string Slice(string fileContent, int lineStart, int lineEnd)
    {
        if (lineStart <= 0 || lineEnd < lineStart) return fileContent;
        var lines = fileContent.Replace("\r\n", "\n").Split('\n');
        var from = Math.Min(lineStart - 1, lines.Length);
        var to = Math.Min(lineEnd, lines.Length);
        return string.Join('\n', lines[from..to]);
    }

    private static string StripComments(string code)
    {
        // Drop /* */, // and # comments but keep strings intact enough for the patterns above.
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        code = Regex.Replace(code, @"(?m)(^|[^:'""\\])//[^\n]*", "$1");
        code = Regex.Replace(code, @"(?m)^\s*#(?!\[)[^\n]*", "");
        return code;
    }

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
