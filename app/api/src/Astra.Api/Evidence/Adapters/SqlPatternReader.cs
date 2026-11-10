using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>
/// The fallback for SQL ScriptDom cannot read (another dialect, a statement it rejects): the objects a
/// script creates and what their bodies mention, found by pattern. It is honest about being a guess —
/// everything it finds is marked as read by pattern and links from it are medium confidence — and it
/// finds the same things a reader would look for first (names, keys, the tables after FROM and INSERT).
/// </summary>
internal static class SqlPatternReader
{
    private const string NamePart = @"(?:\[[^\]]+\]|""[^""]+""|[#@]{0,2}\w+)";
    private const string Name = NamePart + @"(?:\s*\.\s*" + NamePart + @"){0,3}";

    private static readonly Regex Header = new(
        @"^[ \t]*(?<verb>CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)(?<kind>TABLE|VIEW|PROC(?:EDURE)?|FUNCTION|TRIGGER|(?:UNIQUE\s+)?(?:(?:NON)?CLUSTERED\s+)?INDEX)\s+(?<name>" + Name + ")",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex IndexTable = new(@"\bON\s+(?<t>" + Name + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex KeyDefinition = new(
        @"CONSTRAINT\s+(?<n>" + NamePart + @")\s+(?<k>UNIQUE|PRIMARY\s+KEY)[^()]*\((?<c>[^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WriteTarget = new(
        @"\b(?:INSERT\s+(?:INTO\s+)?|UPDATE\s+|DELETE\s+|MERGE\s+(?:INTO\s+)?|TRUNCATE\s+TABLE\s+)(?<t>" + Name + ")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReadSource = new(@"\b(?:FROM|JOIN)\s+(?<t>" + Name + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Execute = new(@"\bEXEC(?:UTE)?\s+(?!@)(?<p>" + Name + @")(?!\s*\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "set", "top", "from", "where", "output", "values", "into", "table", "with", "inserted", "deleted",
        "openquery", "openrowset", "as", "on", "using", "when", "default", "null", "case", "distinct", "exists", "cascade",
    };

    /// <summary>The 0-based positions (in lines) of every statement that starts a CREATE or ALTER of something named.</summary>
    public static List<int> HeaderLines(string text)
    {
        var lines = new List<int>();
        foreach (Match m in Header.Matches(text))
            lines.Add(text.AsSpan(0, m.Index).Count('\n'));
        return lines;
    }

    /// <summary>Reads the object a segment creates, if it starts with one; null for any other text.</summary>
    public static SqlObject? Read(string segment, int startLine, int endLine)
    {
        var m = Header.Match(segment);
        if (!m.Success || m.Index > 0 && segment[..m.Index].Trim().Length > 0) return null;

        var verb = m.Groups["verb"].Value.Trim().ToUpperInvariant();
        var keyword = Regex.Replace(m.Groups["kind"].Value.ToUpperInvariant(), @"\s+", " ");
        var kind = keyword switch
        {
            "TABLE" => verb == "ALTER" ? "alter-table" : "table",
            "VIEW" => "view",
            "PROC" or "PROCEDURE" => "procedure",
            "FUNCTION" => "function",
            "TRIGGER" => "trigger",
            _ => "index",
        };
        var name = Normalise(m.Groups["name"].Value);
        string? table = null;
        if (kind is "alter-table") table = name;
        if (kind is "index" or "trigger")
        {
            var on = IndexTable.Match(segment, m.Index + m.Length);
            if (on.Success) table = Normalise(on.Groups["t"].Value);
        }

        var o = new SqlObject
        {
            Kind = kind,
            Name = name,
            Schema = SchemaOf(m.Groups["name"].Value),
            Table = table,
            StartLine = startLine,
            EndLine = endLine,
            Parsed = false,
        };

        var body = Strip(segment);
        if (kind is "table" or "alter-table" or "index")
        {
            foreach (Match k in KeyDefinition.Matches(body))
                o.Keys.Add(new SqlKey(Unquote(k.Groups["n"].Value), k.Groups["k"].Value.StartsWith("P", StringComparison.OrdinalIgnoreCase) ? "primary" : "unique", Columns(k.Groups["c"].Value)));
            if (kind == "index" && keyword.StartsWith("UNIQUE", StringComparison.Ordinal))
            {
                var cols = Regex.Match(body[(m.Index + m.Length)..], @"\((?<c>[^)]*)\)");
                if (cols.Success) o.Keys.Add(new SqlKey(name, "unique", Columns(cols.Groups["c"].Value)));
            }
        }
        else if (kind is "view" or "procedure")
        {
            ReadBody(o, body[(m.Index + m.Length)..]);
        }
        return o;
    }

    /// <summary>The tables and procedures a fragment of SQL text mentions (a query inside a command line, say).</summary>
    public static SqlObject ScanBody(string sql)
    {
        var o = new SqlObject { Kind = "fragment", Name = "", Parsed = false };
        ReadBody(o, Strip(sql));
        return o;
    }

    private static void ReadBody(SqlObject o, string body)
    {
        // DELETE FROM x names x once, as a write; the FROM must not also make it a read.
        body = Regex.Replace(body, @"\bDELETE\s+FROM\b", "DELETE ", RegexOptions.IgnoreCase);

        foreach (Match w in WriteTarget.Matches(body))
            if (UsableTable(w.Groups["t"].Value) is { } t) SqlObject.AddOnce(o.Writes, t);
        foreach (Match r in ReadSource.Matches(body))
            if (UsableTable(r.Groups["t"].Value) is { } t) SqlObject.AddOnce(o.Reads, t);
        foreach (Match e in Execute.Matches(body))
        {
            var p = Normalise(e.Groups["p"].Value);
            if (!NotNames.Contains(p) && !p.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))
                SqlObject.AddOnce(o.Calls, p);
        }
    }

    private static string? UsableTable(string raw)
    {
        var n = Normalise(raw);
        if (n.Length == 0 || n.StartsWith('#') || n.StartsWith('@') || NotNames.Contains(n)) return null;
        if (n.StartsWith("sys.", StringComparison.OrdinalIgnoreCase) || n.StartsWith("INFORMATION_SCHEMA.", StringComparison.OrdinalIgnoreCase)) return null;
        return n;
    }

    /// <summary>Comments and string literals removed, so a word in either is not taken for a table or a call.</summary>
    public static string Strip(string text)
    {
        var s = Regex.Replace(text, @"/\*.*?\*/", m => Blank(m.Value), RegexOptions.Singleline);
        s = Regex.Replace(s, @"--[^\n]*", " ");
        return Regex.Replace(s, @"'(?:[^']|'')*'", "''");
    }

    // Keeps line breaks, so positions measured on the stripped text still match the original.
    private static string Blank(string s) => string.Concat(s.Select(c => c == '\n' ? '\n' : ' '));

    public static string Normalise(string raw)
    {
        var parts = Regex.Split(raw.Trim(), @"\s*\.\s*").Select(Unquote).Where(p => p.Length > 0).ToList();
        if (parts.Count == 2 && parts[0].Equals("dbo", StringComparison.OrdinalIgnoreCase)) return parts[1];
        return string.Join('.', parts);
    }

    private static string? SchemaOf(string raw)
    {
        var parts = Regex.Split(raw.Trim(), @"\s*\.\s*").Select(Unquote).ToList();
        return parts.Count >= 2 ? parts[^2] : null;
    }

    private static string Unquote(string part) => part.Trim().Trim('[', ']', '"').Trim();

    private static IReadOnlyList<string> Columns(string list) =>
        list.Split(',')
            .Select(c => Regex.Replace(c, @"\b(ASC|DESC)\b", "", RegexOptions.IgnoreCase))
            .Select(Unquote)
            .Where(c => c.Length > 0)
            .ToList();
}
