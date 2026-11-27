using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Astra.Api.Assembly;

/// <summary>application.properties of many units → one: equal keys once, a key two units set differently keeps the first value and is reported.</summary>
internal static class PropertiesMerger
{
    public static string Merge(IReadOnlyList<(string Unit, string Text)> files, IReadOnlyList<KeyValuePair<string, string>> fixedValues, List<AssemblyConflict> conflicts)
    {
        var fixedKeys = fixedValues.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var values = new List<(string Key, string Value, string Unit)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (unit, text) in files)
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or '!') continue;
                var sep = line.IndexOfAny(new[] { '=', ':' });
                if (sep <= 0) continue;
                var key = line[..sep].Trim();
                var value = line[(sep + 1)..].Trim();
                if (fixedKeys.Contains(key)) continue;
                if (index.TryGetValue(key, out var at))
                {
                    if (values[at].Value != value && reported.Add(key))
                        conflicts.Add(new AssemblyConflict("property", key,
                            $"{key} is \"{values[at].Value}\" in one unit and \"{value}\" in another; the first is kept.", new[] { values[at].Unit, unit }));
                    continue;
                }
                index[key] = values.Count;
                values.Add((key, value, unit));
            }
        var sb = new StringBuilder();
        sb.AppendLine("# Merged by Astra from the code units' application.properties; disagreements are listed in ASSEMBLY.md.");
        foreach (var f in fixedValues) sb.AppendLine($"{f.Key}={f.Value}");
        foreach (var v in values) sb.AppendLine($"{v.Key}={v.Value}");
        return sb.ToString();
    }
}

/// <summary>
/// schema.sql of many units → one. Units describe the same legacy table from what each one reads, so two units'
/// CREATE TABLE for one table usually list different columns: the merged table has every column any unit needs (the
/// first definition of a column wins; a different type is reported), and a column keeps NOT NULL only when every unit
/// that has the table declares it so — otherwise one unit's inserts would break another's. Tables are ordered so a
/// foreign key never points at a table created later; other statements are kept once each, in order.
/// </summary>
public static class SchemaMerger
{
    private static readonly Regex CreateTable = new(
        @"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([`""\[]?[\w.]+[`""\]]?)\s*\((.*)\)\s*([^()]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex References = new(@"\bREFERENCES\s+([`""\[]?[\w.]+[`""\]]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> ConstraintWords = new(StringComparer.OrdinalIgnoreCase)
        { "PRIMARY", "FOREIGN", "CONSTRAINT", "UNIQUE", "CHECK", "INDEX", "KEY" };

    private sealed class Table
    {
        public required string Name { get; init; }
        public required string DisplayName { get; init; }
        public string Tail { get; set; } = "";
        public List<string> Order { get; } = new();
        public Dictionary<string, string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> NotNullVotes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Constraints { get; } = new();
        public List<string> Units { get; } = new();
        public List<string> Added { get; } = new();
    }

    public static string Merge(IReadOnlyList<(string Unit, string Text)> files, List<AssemblyConflict> conflicts, List<SharedItem> shared, List<string> notes)
    {
        var tables = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
        var tableOrder = new List<string>();
        var others = new List<string>();
        var seenOthers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (unit, text) in files)
            foreach (var stmt in Statements(text))
            {
                var m = CreateTable.Match(stmt);
                if (!m.Success)
                {
                    if (seenOthers.Add(Squash(stmt))) others.Add(stmt);
                    continue;
                }
                var key = Bare(m.Groups[1].Value);
                var parts = SplitTopLevel(m.Groups[2].Value);
                var columns = new List<(string Name, string Def)>();
                var constraints = new List<string>();
                foreach (var p in parts)
                {
                    var first = Regex.Match(p, @"^[`""\[]?(\w+)").Groups[1].Value;
                    if (first.Length == 0 || ConstraintWords.Contains(first)) constraints.Add(p.Trim());
                    else columns.Add((first, p.Trim()));
                }

                if (!tables.TryGetValue(key, out var t))
                {
                    t = new Table { Name = key, DisplayName = m.Groups[1].Value, Tail = m.Groups[3].Value.Trim() };
                    tables[key] = t;
                    tableOrder.Add(key);
                }
                if (!t.Units.Contains(unit)) t.Units.Add(unit);
                // A table has one primary key. The first unit's key (inline on a column or a PRIMARY KEY (...) clause)
                // is kept; a later unit's different key is dropped and reported, never added beside it.
                var hadKey = HasPrimaryKey(t);
                var incomingKey = DescribeKey(columns, constraints);
                if (hadKey && incomingKey is not null && !string.Equals(incomingKey, DescribeKey(t), StringComparison.OrdinalIgnoreCase))
                    conflicts.Add(new AssemblyConflict("primary-key", t.DisplayName,
                        $"one unit keys it on {DescribeKey(t)} and another on {incomingKey}; the first is kept.", t.Units.ToList()));
                foreach (var (name, rawDef) in columns)
                {
                    var def = rawDef;
                    if ((hadKey || HasPrimaryKey(t)) && Regex.IsMatch(def, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase))
                        def = Regex.Replace(def, @"\s+PRIMARY\s+KEY\b", " NOT NULL", RegexOptions.IgnoreCase);
                    var notNull = Regex.IsMatch(def, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase) || Regex.IsMatch(def, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase);
                    t.NotNullVotes[name] = t.NotNullVotes.GetValueOrDefault(name) + (notNull ? 1 : 0);
                    if (t.Columns.TryGetValue(name, out var existing))
                    {
                        if (TypeOf(existing) != TypeOf(def))
                            conflicts.Add(new AssemblyConflict("column", $"{t.DisplayName}.{name}",
                                $"declared as {TypeOf(existing)} by one unit and {TypeOf(def)} by another; the first is kept.", t.Units.ToList()));
                        continue;
                    }
                    if (t.Units.Count > 1) t.Added.Add(name);
                    t.Columns[name] = def;
                    t.Order.Add(name);
                }
                foreach (var c in constraints)
                {
                    var isPk = IsKeyClause(c);
                    if (isPk && (hadKey || HasPrimaryKey(t))) continue;
                    // Spacing is not a difference: "REFERENCES t (id)" and "REFERENCES t(id)" are one constraint.
                    if (!t.Constraints.Any(x => Tight(x) == Tight(c))) t.Constraints.Add(c);
                }
            }

        // Foreign keys first: a table is written after the tables it references (when they are in this schema).
        var written = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string key)
        {
            if (written.Contains(key, StringComparer.OrdinalIgnoreCase) || !visiting.Add(key)) return;
            var t = tables[key];
            foreach (var r in t.Columns.Values.Concat(t.Constraints).SelectMany(x => References.Matches(x).Select(m => Bare(m.Groups[1].Value))))
                if (tables.ContainsKey(r) && !string.Equals(r, key, StringComparison.OrdinalIgnoreCase)) Visit(r);
            written.Add(key);
        }
        foreach (var k in tableOrder) Visit(k);

        var sb = new StringBuilder();
        sb.AppendLine("-- Merged by Astra from the code units' schema.sql: one CREATE TABLE per table with every column any unit needs.");
        foreach (var key in written)
        {
            var t = tables[key];
            var lines = new List<string>();
            foreach (var col in t.Order)
            {
                var def = t.Columns[col];
                var everyone = t.NotNullVotes.GetValueOrDefault(col) == t.Units.Count;
                if (!everyone && t.Units.Count > 1 && !Regex.IsMatch(def, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase))
                    def = Regex.Replace(def, @"\s+NOT\s+NULL\b", "", RegexOptions.IgnoreCase);
                lines.Add("    " + def);
            }
            lines.AddRange(t.Constraints.Select(c => "    " + c));
            sb.AppendLine();
            sb.AppendLine($"CREATE TABLE IF NOT EXISTS {t.DisplayName} (");
            sb.AppendLine(string.Join(",\n", lines));
            sb.AppendLine(")" + (t.Tail.Length > 0 ? " " + t.Tail : "") + ";");
            if (t.Units.Count > 1)
                shared.Add(new SharedItem("table", t.DisplayName, "schema.sql", t.Units.ToList()));
            if (t.Added.Count > 0)
                notes.Add($"Table {t.DisplayName} merges the columns of {t.Units.Count} units ({string.Join(", ", t.Added)} came from a later unit); columns not every unit declares NOT NULL are nullable.");
        }
        foreach (var o in others)
        {
            sb.AppendLine();
            sb.AppendLine(o.TrimEnd() + ";");
        }
        return sb.ToString();
    }

    /// <summary>data.sql and the like: every distinct statement once, in the order the units give them.</summary>
    public static string MergeStatements(IReadOnlyList<(string Unit, string Text)> files, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine($"-- Merged by Astra from the code units' {name}.");
        foreach (var (_, text) in files)
            foreach (var s in Statements(text))
                if (seen.Add(Squash(s))) sb.AppendLine(s.TrimEnd() + ";");
        return sb.ToString();
    }

    /// <summary>Statements split on semicolons outside quotes; comment-only lines dropped.</summary>
    internal static IEnumerable<string> Statements(string sql)
    {
        var noLineComments = string.Join("\n", sql.Replace("\r\n", "\n").Split('\n').Select(l =>
        {
            var i = l.IndexOf("--", StringComparison.Ordinal);
            return i >= 0 && !InQuote(l, i) ? l[..i] : l;
        }));
        var sb = new StringBuilder();
        var quote = '\0';
        foreach (var ch in noLineComments)
        {
            if (quote != '\0') { if (ch == quote) quote = '\0'; sb.Append(ch); continue; }
            if (ch is '\'' or '"') { quote = ch; sb.Append(ch); continue; }
            if (ch == ';')
            {
                var s = sb.ToString().Trim();
                if (s.Length > 0) yield return s;
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        var last = sb.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    private static bool InQuote(string line, int pos) => line[..pos].Count(c => c == '\'') % 2 == 1;

    internal static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var sb = new StringBuilder();
        var quote = '\0';
        foreach (var ch in body)
        {
            if (quote != '\0') { if (ch == quote) quote = '\0'; sb.Append(ch); continue; }
            if (ch is '\'' or '"') { quote = ch; sb.Append(ch); continue; }
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (ch == ',' && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.ToString().Trim().Length > 0) parts.Add(sb.ToString());
        return parts.Select(p => Regex.Replace(p.Trim(), @"\s+", " ")).Where(p => p.Length > 0).ToList();
    }

    private static string TypeOf(string columnDef)
    {
        var m = Regex.Match(columnDef, @"^[`""\[]?\w+[`""\]]?\s+([A-Za-z]+(?:\s*\([^)]*\))?)");
        return m.Success ? Regex.Replace(m.Groups[1].Value.ToUpperInvariant(), @"\s+", "") : "?";
    }

    private static string Bare(string name) => name.Trim('`', '"', '[', ']').ToLowerInvariant();
    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
    private static string Tight(string s) => Regex.Replace(s, @"\s+", "").ToLowerInvariant();

    /// <summary>A table-level key clause: PRIMARY KEY (...) or CONSTRAINT name PRIMARY KEY (...).</summary>
    private static bool IsKeyClause(string c) =>
        Regex.IsMatch(c, @"^(?:CONSTRAINT\s+[`""\[]?\w+[`""\]]?\s+)?PRIMARY\s+KEY\b", RegexOptions.IgnoreCase);

    private static bool HasPrimaryKey(Table t) =>
        t.Constraints.Any(IsKeyClause) || t.Columns.Values.Any(d => Regex.IsMatch(d, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase));

    private static string? DescribeKey(Table t) =>
        DescribeKey(t.Columns.Select(kv => (kv.Key, kv.Value)).ToList(), t.Constraints);

    /// <summary>The key's columns, e.g. "(rowid)" or "(fk_soc, fk_user)"; null when the statement declares none.</summary>
    private static string? DescribeKey(IReadOnlyList<(string Name, string Def)> columns, IReadOnlyList<string> constraints)
    {
        var inline = columns.FirstOrDefault(c => Regex.IsMatch(c.Def, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase));
        if (inline.Name is not null) return $"({inline.Name.ToLowerInvariant()})";
        var clause = constraints.FirstOrDefault(IsKeyClause);
        if (clause is null) return null;
        var cols = Regex.Match(clause, @"PRIMARY\s+KEY\s*\(([^)]*)\)", RegexOptions.IgnoreCase).Groups[1].Value;
        return "(" + string.Join(", ", cols.Split(',').Select(x => Bare(x.Trim()))) + ")";
    }
}

/// <summary>
/// The units' poms → one: the newest Spring Boot parent, every dependency once (newest explicit version; the widest
/// scope), every build plugin once, the highest java.version. Each disagreement is reported. The sidecar builds
/// offline, so the merge only ever uses what some unit already used.
/// </summary>
internal static class PomMerger
{
    public static string Merge(IReadOnlyList<(string Unit, string? Pom)> poms, string groupId, string artifactId, string moduleName,
        List<AssemblyConflict> conflicts, List<string> notes)
    {
        var docs = new List<(string Unit, XDocument Doc)>();
        foreach (var (unit, pom) in poms)
        {
            if (string.IsNullOrWhiteSpace(pom)) { notes.Add($"{unit} had no pom.xml; its dependencies are not represented."); continue; }
            try { docs.Add((unit, XDocument.Parse(pom))); }
            catch (Exception) { notes.Add($"{unit}'s pom.xml could not be read; its dependencies are not represented."); }
        }
        if (docs.Count == 0) throw new InvalidOperationException("No code unit has a readable pom.xml.");

        XNamespace ns = docs[0].Doc.Root!.Name.Namespace;
        string? V(XElement? parent, string name) => (parent?.Element(ns + name) ?? parent?.Element(name))?.Value.Trim();

        var result = new XDocument(docs[0].Doc);
        var root = result.Root!;

        // Parent: the newest version any unit used.
        var parents = docs.Select(d => (d.Unit, P: d.Doc.Root!.Element(ns + "parent"))).Where(x => x.P is not null).ToList();
        if (parents.Count > 0)
        {
            var newest = parents.OrderByDescending(x => Ver(V(x.P, "version"))).First();
            var versions = parents.Select(x => V(x.P, "version")).Distinct().ToList();
            if (versions.Count > 1)
                conflicts.Add(new AssemblyConflict("parent", V(newest.P, "artifactId") ?? "parent",
                    $"units use parent versions {string.Join(", ", versions)}; {V(newest.P, "version")} is used.", parents.Select(x => x.Unit).ToList()));
            root.Element(ns + "parent")?.ReplaceWith(new XElement(newest.P!));
        }

        SetChild(root, ns, "groupId", groupId);
        SetChild(root, ns, "artifactId", artifactId);
        SetChild(root, ns, "version", "0.1.0");
        SetChild(root, ns, "name", moduleName);
        SetChild(root, ns, "description", $"{moduleName}: one application assembled by Astra from {poms.Count} code units.");

        // Properties: union; java.version the highest; other disagreements keep the first.
        var props = new Dictionary<string, (string Value, string Unit)>(StringComparer.Ordinal);
        var propOrder = new List<string>();
        foreach (var (unit, doc) in docs)
            foreach (var p in doc.Root!.Element(ns + "properties")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var k = p.Name.LocalName;
                var v = p.Value.Trim();
                if (!props.TryGetValue(k, out var cur)) { props[k] = (v, unit); propOrder.Add(k); continue; }
                if (cur.Value == v) continue;
                if (k == "java.version")
                {
                    if (Ver(v) > Ver(cur.Value)) props[k] = (v, unit);
                    continue;
                }
                conflicts.Add(new AssemblyConflict("pom-property", k, $"{k} is {cur.Value} in one unit and {v} in another; the first is kept.", new[] { cur.Unit, unit }));
            }
        if (propOrder.Count > 0)
        {
            var pe = root.Element(ns + "properties");
            if (pe is null) { pe = new XElement(ns + "properties"); root.Element(ns + "dependencies")?.AddBeforeSelf(pe); }
            pe.RemoveNodes();
            foreach (var k in propOrder) pe.Add(new XElement(ns + k, props[k].Value));
        }

        // Dependencies: by groupId:artifactId(:classifier).
        MergeList(docs.Select(d => (d.Unit, d.Doc.Root!.Element(ns + "dependencies"))).ToList(), root, ns, "dependencies", "dependency", conflicts, isPlugin: false);
        var dm = docs.Select(d => (d.Unit, d.Doc.Root!.Element(ns + "dependencyManagement")?.Element(ns + "dependencies"))).Where(x => x.Item2 is not null).ToList();
        if (dm.Count > 0)
        {
            var dmEl = root.Element(ns + "dependencyManagement") ?? new XElement(ns + "dependencyManagement");
            if (dmEl.Parent is null) root.Element(ns + "dependencies")?.AddBeforeSelf(dmEl);
            MergeList(dm, dmEl, ns, "dependencies", "dependency", conflicts, isPlugin: false);
        }

        // Build plugins: by groupId:artifactId; the first configuration is kept.
        var builds = docs.Select(d => (d.Unit, d.Doc.Root!.Element(ns + "build")?.Element(ns + "plugins"))).ToList();
        if (builds.Any(b => b.Item2 is not null))
        {
            var build = root.Element(ns + "build") ?? new XElement(ns + "build");
            if (build.Parent is null) root.Add(build);
            MergeList(builds, build, ns, "plugins", "plugin", conflicts, isPlugin: true);
        }

        foreach (var repoKind in new[] { "repositories", "pluginRepositories" })
        {
            var all = docs.SelectMany(d => d.Doc.Root!.Element(ns + repoKind)?.Elements() ?? Enumerable.Empty<XElement>())
                .GroupBy(r => V(r, "url") ?? r.ToString()).Select(g => new XElement(g.First())).ToList();
            root.Element(ns + repoKind)?.Remove();
            if (all.Count > 0) root.Add(new XElement(ns + repoKind, all));
        }

        var decl = result.Declaration?.ToString() ?? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        return decl + "\n" + root.ToString() + "\n";
    }

    private static void MergeList(IReadOnlyList<(string Unit, XElement? List)> lists, XElement owner, XNamespace ns, string listName, string itemName,
        List<AssemblyConflict> conflicts, bool isPlugin)
    {
        string? V(XElement x, string n) => x.Element(ns + n)?.Value.Trim();
        var merged = new List<XElement>();
        var byKey = new Dictionary<string, (XElement El, string Unit)>(StringComparer.Ordinal);
        foreach (var (unit, list) in lists)
            foreach (var item in list?.Elements(ns + itemName) ?? Enumerable.Empty<XElement>())
            {
                var key = $"{V(item, "groupId") ?? (isPlugin ? "org.apache.maven.plugins" : "")}:{V(item, "artifactId")}:{V(item, "classifier")}:{V(item, "type")}";
                if (!byKey.TryGetValue(key, out var cur))
                {
                    var copy = new XElement(item);
                    byKey[key] = (copy, unit);
                    merged.Add(copy);
                    continue;
                }
                var v1 = V(cur.El, "version");
                var v2 = V(item, "version");
                if (v1 != v2 && v2 is not null)
                {
                    var keepNew = !isPlugin && (v1 is null ? false : Ver(v2) > Ver(v1));
                    conflicts.Add(new AssemblyConflict(isPlugin ? "plugin" : "dependency", V(item, "artifactId") ?? key,
                        $"version {v1 ?? "(managed)"} in one unit and {v2} in another; {(keepNew ? v2 : v1 ?? "(managed)")} is used.", new[] { cur.Unit, unit }));
                    if (keepNew) cur.El.Element(ns + "version")!.Value = v2;
                }
                if (!isPlugin)
                {
                    var s1 = V(cur.El, "scope") ?? "compile";
                    var s2 = V(item, "scope") ?? "compile";
                    if (ScopeRank(s2) > ScopeRank(s1))
                    {
                        if (s2 == "compile") cur.El.Element(ns + "scope")?.Remove();
                        else if (cur.El.Element(ns + "scope") is { } se) se.Value = s2;
                        else cur.El.Add(new XElement(ns + "scope", s2));
                    }
                }
            }
        var target = owner.Element(ns + listName);
        if (target is null) { target = new XElement(ns + listName); owner.Add(target); }
        target.RemoveNodes();
        foreach (var m in merged) target.Add(m);
    }

    private static int ScopeRank(string scope) => scope switch
    {
        "compile" => 4,
        "runtime" => 3,
        "provided" => 2,
        "test" => 1,
        _ => 0,
    };

    private static void SetChild(XElement root, XNamespace ns, string name, string value)
    {
        var el = root.Element(ns + name);
        if (el is null) root.Element(ns + "parent")?.AddAfterSelf(el = new XElement(ns + name));
        if (el is null) root.AddFirst(el = new XElement(ns + name));
        el.Value = value;
    }

    /// <summary>"3.2.5" → comparable; non-numeric parts count as 0.</summary>
    internal static Version Ver(string? v)
    {
        var nums = Regex.Matches(v ?? "", @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : 0).Take(4).ToList();
        while (nums.Count < 2) nums.Add(0);
        return nums.Count switch
        {
            2 => new Version(nums[0], nums[1]),
            3 => new Version(nums[0], nums[1], nums[2]),
            _ => new Version(nums[0], nums[1], nums[2], nums[3]),
        };
    }
}
