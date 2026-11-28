using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>
/// The seeded database of an equivalence run: the generated package's own <c>schema.sql</c> is the one schema both
/// sides get — as written for the Java side (H2, MySQL mode) and translated for the PHP side (SQLite) — and each vector
/// brings its seed rows. After the call both sides dump every table, and <see cref="CompareTables"/> compares the
/// rows as multisets. Pure: text in, text out.
/// </summary>
public static partial class EquivalenceDatabase
{
    [GeneratedRegex(@"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[`""\[]?([\w.]+)[`""\]]?\s*\((.*)\)\s*([^()]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"\b(AUTO_INCREMENT|IDENTITY(\s*\([^)]*\))?|GENERATED\s+(ALWAYS|BY\s+DEFAULT)\s+AS\s+IDENTITY(\s*\([^)]*\))?)", RegexOptions.IgnoreCase)]
    private static partial Regex Identity();

    /// <summary>SQL text as statements: semicolons outside quotes and comments; comments dropped.</summary>
    public static List<string> Statements(string? sql)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(sql)) return list;
        var sb = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (quote is null)
            {
                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; sb.Append('\n'); continue; }
                if (c == '#' ) { while (i < sql.Length && sql[i] != '\n') i++; sb.Append('\n'); continue; }
                if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; sb.Append(' '); continue; }
                if (c is '\'' or '"' or '`') quote = c;
                else if (c == ';') { Flush(); continue; }
            }
            else if (c == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote) { sb.Append(c).Append(c); i++; continue; }
                quote = null;
            }
            sb.Append(c);
        }
        Flush();
        return list;

        void Flush()
        {
            var s = sb.ToString().Trim();
            if (s.Length > 0) list.Add(s);
            sb.Clear();
        }
    }

    /// <summary>The tables a schema creates, in order, lower-cased.</summary>
    public static List<string> Tables(IEnumerable<string> statements) =>
        statements.Select(s => CreateTable().Match(s)).Where(m => m.Success)
            .Select(m => Bare(m.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The schema as SQLite takes it: an identity primary key becomes <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> (the
    /// only auto-numbering SQLite has), other identity words, UNSIGNED, column comments, charsets and table options
    /// go, ENUM/SET become TEXT, MySQL's inline KEY/INDEX clauses are dropped (a UNIQUE KEY stays as UNIQUE). Types are
    /// otherwise kept: SQLite accepts any type name. Statements other than CREATE TABLE pass through, minus SET/USE.
    /// </summary>
    public static List<string> ToSqlite(IEnumerable<string> statements)
    {
        var outList = new List<string>();
        foreach (var raw in statements)
        {
            var s = raw.Trim();
            if (Regex.IsMatch(s, @"^(SET|USE|LOCK|UNLOCK)\b", RegexOptions.IgnoreCase)) continue;
            var m = CreateTable().Match(s);
            if (!m.Success)
            {
                outList.Add(Regex.Replace(s, @"\s+USING\s+(BTREE|HASH)\b", "", RegexOptions.IgnoreCase));
                continue;
            }
            var name = m.Groups[1].Value;
            var parts = SplitTopLevel(m.Groups[2].Value);
            var defs = new List<string>();
            var cols = new List<(string Name, string Def)>();
            var clauses = new List<string>();
            foreach (var p0 in parts)
            {
                var p = p0.Trim();
                var first = Regex.Match(p, @"^[`""\[]?(\w+)").Groups[1].Value;
                if (Regex.IsMatch(first, @"^(PRIMARY|FOREIGN|CONSTRAINT|UNIQUE|CHECK|KEY|INDEX|FULLTEXT|SPATIAL)$", RegexOptions.IgnoreCase)) clauses.Add(p.Trim());
                else if (first.Length > 0) cols.Add((first, p.Trim()));
            }
            var pkClause = clauses.FirstOrDefault(c => Regex.IsMatch(c, @"^(CONSTRAINT\s+\S+\s+)?PRIMARY\s+KEY\b", RegexOptions.IgnoreCase));
            var pkCols = pkClause is null ? new List<string>()
                : Regex.Match(pkClause, @"\(([^)]*)\)").Groups[1].Value.Split(',').Select(x => Bare(x.Trim())).Where(x => x.Length > 0).ToList();
            var dropPkClause = false;
            foreach (var (col, def0) in cols)
            {
                var def = def0;
                var isIdentity = Identity().IsMatch(def);
                var inlinePk = Regex.IsMatch(def, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase);
                var keyedByClause = pkCols.Count == 1 && pkCols[0] == Bare(col);
                if (isIdentity && (inlinePk || keyedByClause))
                {
                    defs.Add($"{Quote(col)} INTEGER PRIMARY KEY AUTOINCREMENT");
                    if (keyedByClause) dropPkClause = true;
                    continue;
                }
                def = Identity().Replace(def, "");
                def = Regex.Replace(def, @"\bUNSIGNED\b|\bZEROFILL\b", "", RegexOptions.IgnoreCase);
                def = Regex.Replace(def, @"\bON\s+UPDATE\s+CURRENT_TIMESTAMP(\(\d*\))?", "", RegexOptions.IgnoreCase);
                def = Regex.Replace(def, @"\bCOMMENT\s+'(?:[^']|'')*'", "", RegexOptions.IgnoreCase);
                def = Regex.Replace(def, @"\b(CHARACTER\s+SET|CHARSET|COLLATE)\s+\w+", "", RegexOptions.IgnoreCase);
                def = Regex.Replace(def, @"\b(ENUM|SET)\s*\((?:[^()']|'(?:[^']|'')*')*\)", "TEXT", RegexOptions.IgnoreCase);
                def = Regex.Replace(def, @"\bDEFAULT\s+(NOW|CURRENT_TIMESTAMP)\s*\(\s*\d*\s*\)", "DEFAULT CURRENT_TIMESTAMP", RegexOptions.IgnoreCase);
                defs.Add(Regex.Replace(def, @"\s{2,}", " ").Trim());
            }
            foreach (var c in clauses)
            {
                if (dropPkClause && c == pkClause) continue;
                if (Regex.IsMatch(c, @"^(KEY|INDEX|FULLTEXT|SPATIAL)\b", RegexOptions.IgnoreCase)) continue;
                var u = Regex.Match(c, @"^UNIQUE\s+(?:KEY|INDEX)\s+(?:[`""\[]?\w+[`""\]]?\s*)?(\(.*\))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                defs.Add(u.Success ? "UNIQUE " + u.Groups[1].Value : c);
            }
            outList.Add($"CREATE TABLE IF NOT EXISTS {name} (\n  {string.Join(",\n  ", defs)}\n)");
        }
        return outList;
    }

    /// <summary>
    /// The H2 URL options for the Java side: MySQL mode, lower-case names, and the column names that are H2 keywords
    /// (value, key, user …) released so the package's schema loads as it does in its own tests.
    /// </summary>
    public static string H2NonKeywords(string schemaSql) => string.Join(",", Assembly.H2Keywords.UsedAsNames(schemaSql));

    // ── comparing the tables after the call ─────────────────────────────────

    /// <summary>
    /// Null when every table holds the same rows on both sides (as multisets); otherwise a readable account of the
    /// first table that differs. Values are normalised: numbers by value (a numeric string equals the number), true/false
    /// as 1/0, a missing column as null; date-times and epoch seconds within an hour of the run are treated as "now"
    /// (both sides stamp their own clock).
    /// </summary>
    public static string? CompareTables(JsonObject? php, JsonObject? java, DateTimeOffset? now = null)
    {
        if (php is null || java is null) return null;
        var at = now ?? DateTimeOffset.UtcNow;
        foreach (var table in php.Select(kv => kv.Key).Union(java.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (table.StartsWith("__", StringComparison.Ordinal)) continue;
            var a = php[table] as JsonArray;
            var b = java[table] as JsonArray;
            if (a is null || b is null) continue;   // one side could not read it: reported in the notes, not judged here
            var ra = a.OfType<JsonObject>().Select(r => Canon(r, at)).ToList();
            var rb = b.OfType<JsonObject>().Select(r => Canon(r, at)).ToList();
            var onlyA = Minus(ra, rb);
            var onlyB = Minus(rb, ra);
            if (onlyA.Count == 0 && onlyB.Count == 0) continue;
            var sb = new StringBuilder($"table {table}: PHP has {ra.Count} row(s), Java {rb.Count}");
            if (onlyA.Count > 0) sb.Append($"; only in PHP: {string.Join(" ", onlyA.Take(2))}{(onlyA.Count > 2 ? $" (+{onlyA.Count - 2})" : "")}");
            if (onlyB.Count > 0) sb.Append($"; only in Java: {string.Join(" ", onlyB.Take(2))}{(onlyB.Count > 2 ? $" (+{onlyB.Count - 2})" : "")}");
            return sb.ToString();
        }
        return null;
    }

    private static List<string> Minus(List<string> a, List<string> b)
    {
        var left = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var outList = new List<string>();
        foreach (var x in a)
        {
            if (left.TryGetValue(x, out var n) && n > 0) left[x] = n - 1;
            else outList.Add(x);
        }
        return outList;
    }

    private static string Canon(JsonObject row, DateTimeOffset now)
    {
        var parts = row.Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal))
            .Select(kv => (Key: kv.Key.ToLowerInvariant(), Value: Value(kv.Value, now)))
            .Where(p => p.Value != "null")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}");
        return "{" + string.Join(", ", parts) + "}";
    }

    private static string Value(JsonNode? n, DateTimeOffset now)
    {
        if (n is null) return "null";
        if (n is not JsonValue v) return n.ToJsonString();
        if (v.TryGetValue<bool>(out var b)) return b ? "1" : "0";
        decimal? num = null;
        if (v.TryGetValue<decimal>(out var d)) num = d;
        else if (v.TryGetValue<double>(out var db) && double.IsFinite(db)) num = (decimal)db;
        else if (v.TryGetValue<long>(out var l)) num = l;
        else if (v.TryGetValue<string>(out var s))
        {
            var t = s.Trim();
            if (Regex.IsMatch(t, @"^[+-]?(\d+\.?\d*|\.\d+)$") && decimal.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) num = p;
            else if (DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) && Regex.IsMatch(t, @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}"))
                return Math.Abs((dt - now).TotalHours) <= 1 ? "<now>" : dt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            else return "'" + s + "'";
        }
        if (num is { } x)
        {
            var secs = now.ToUnixTimeSeconds();
            if (x == decimal.Truncate(x) && Math.Abs(x - secs) <= 3600) return "<now>";
            return x.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        }
        return v.ToJsonString();
    }

    private static string Quote(string col) => col.StartsWith('"') || col.StartsWith('`') ? col : $"\"{col}\"";

    private static string Bare(string name) => name.Trim('`', '"', '[', ']').ToLowerInvariant();

    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(body[start..i]); start = i + 1; }
        }
        parts.Add(body[start..]);
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }
}
