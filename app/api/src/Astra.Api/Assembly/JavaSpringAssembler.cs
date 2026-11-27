using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Assembly;

/// <summary>
/// java-spring: many Spring Boot packages (each with its own pom, application class, package and schema) become one
/// Maven project with one application. Each unit keeps its code in its own Java package under the module's base
/// package, so nothing is renamed inside a unit; classes several units carry identically move once to
/// <c>&lt;base&gt;.common</c>; the per-unit application classes become plain configurations; poms, schema.sql and
/// application.properties are merged with every disagreement written into the report. Bean names are fully qualified
/// so two units' <c>CustomerService</c> do not collide, and each unit's controller advice only advises its own
/// controllers.
/// </summary>
public sealed class JavaSpringAssembler : IModuleAssembler
{
    public string TargetStack => "java-spring";

    private const string MainJava = "src/main/java/";
    private const string TestJava = "src/test/java/";
    private const string MainRes = "src/main/resources/";
    private const string TestRes = "src/test/resources/";
    /// <summary>Test-only application each unit gets, so its tests start that unit alone.</summary>
    public const string UnitTestAppName = "UnitTestApplication";

    private static readonly Regex PackageDecl = new(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TypeDecl = new(@"\b(class|interface|enum|record|@interface)\s+([A-Z]\w*)", RegexOptions.Compiled);
    private static readonly Regex SpringBootApp = new(@"@SpringBootApplication(\s*\([^)]*\))?", RegexOptions.Compiled);
    private static readonly Regex AdviceNoArgs = new(@"@(RestControllerAdvice|ControllerAdvice)(?!\s*\()", RegexOptions.Compiled);
    private static readonly Regex BeanMethod = new(@"@Bean\b[^;{]*?\s(\w+)\s*\(", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default",
        "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import",
        "instanceof", "int", "interface", "long", "native", "new", "package", "private", "protected", "public", "return",
        "short", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try",
        "void", "volatile", "while", "true", "false", "null", "record", "var", "yield", "common",
    };

    private sealed class Unit
    {
        public required UnitPackage Source { get; init; }
        public required string Seg { get; init; }
        public required string NewRoot { get; init; }
        public required string OrigRoot { get; init; }
        public Dictionary<string, string> PackageMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal); // root-relative paths
        public string? AppSimpleName { get; set; }
    }

    public AssembledModule Assemble(IReadOnlyList<UnitPackage> units, AssemblyOptions options)
    {
        if (units.Count == 0) throw new ArgumentException("At least one code unit is needed.", nameof(units));

        var slug = ModuleAssemblers.Slug(options.ModuleName);
        var basePkg = options.BasePackage is { Length: > 0 } bp && Regex.IsMatch(bp, @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")
            ? bp
            : "com.astra." + Segment(slug.Replace('-', '_'));
        var appSimple = Pascal(slug) + "Application";
        var appFqn = basePkg + "." + appSimple;
        var commonPkg = basePkg + ".common";
        var artifactId = slug + "-service";

        var conflicts = new List<AssemblyConflict>();
        var shared = new List<SharedItem>();
        var notes = new List<string>();
        var output = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // ── 1. Units: their package root, a unique segment, and files relative to the Maven project root ──
        var usedSegs = new HashSet<string>(StringComparer.Ordinal) { "common" };
        var parsed = new List<Unit>();
        foreach (var u in units)
        {
            var files = Rooted(u.Files);
            var javaPkgs = files.Where(f => f.Key.EndsWith(".java", StringComparison.Ordinal))
                .Select(f => PackageDecl.Match(f.Value)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
            var origRoot = javaPkgs.Count == 0 ? "" : javaPkgs.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).First();
            var seg = Segment(origRoot.Length > 0 ? origRoot.Split('.')[^1] : u.UnitName);
            var baseSeg = seg;
            for (var i = 2; !usedSegs.Add(seg); i++) seg = baseSeg + i;
            var unit = new Unit { Source = u, Seg = seg, OrigRoot = origRoot, NewRoot = basePkg + "." + seg };
            foreach (var kv in files) unit.Files[kv.Key] = kv.Value;
            foreach (var p in javaPkgs)
            {
                if (origRoot.Length > 0 && (p == origRoot || p.StartsWith(origRoot + ".", StringComparison.Ordinal)))
                    unit.PackageMap[p] = unit.NewRoot + p[origRoot.Length..];
                else
                    unit.PackageMap[p] = unit.NewRoot + ".x" + Segment(p.Replace('.', '_'));
            }
            parsed.Add(unit);
        }

        // ── 2. Classes several units carry identically (after their own package is set aside) move to common ──
        var rootClasses = parsed.ToDictionary(u => u, u => u.Files
            .Where(f => f.Key.StartsWith(MainJava, StringComparison.Ordinal) && f.Key.EndsWith(".java", StringComparison.Ordinal)
                        && PackageOf(f.Value) == u.OrigRoot && u.OrigRoot.Length > 0)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f.Key), f => f.Value, StringComparer.Ordinal));
        foreach (var u in parsed)
            foreach (var (name, content) in rootClasses[u].ToList())
                if (SpringBootApp.IsMatch(content)) { u.AppSimpleName = name; rootClasses[u].Remove(name); }

        string Norm(Unit u, string content) => Regex.Replace(ReplacePackage(content, u.OrigRoot, "__ROOT__"), @"\s+", " ").Trim();
        var groups = parsed.SelectMany(u => rootClasses[u].Select(c => (Unit: u, Name: c.Key, Body: Norm(u, c.Value))))
            .GroupBy(x => (x.Name, x.Body)).Where(g => g.Count() >= 2).ToList();
        // A class may only move if every class of its own package it mentions moves with it (else common would point back into one unit).
        var moving = groups.SelectMany(g => g.Select(x => (x.Unit, x.Name))).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var (u, name) in moving.ToList())
            {
                var mentions = rootClasses[u].Keys.Where(other => other != name && Regex.IsMatch(rootClasses[u][name], $@"\b{Regex.Escape(other)}\b"));
                if (mentions.Any(m => !moving.Contains((u, m)))) { moving.Remove((u, name)); changed = true; }
            }
            // A group is all-or-nothing per body: if one member was dropped, the others stay unless still >= 2.
            foreach (var g in groups)
            {
                var left = g.Where(x => moving.Contains((x.Unit, x.Name))).ToList();
                if (left.Count == 1) { moving.Remove((left[0].Unit, left[0].Name)); changed = true; }
            }
        } while (changed);

        var movedByUnit = parsed.ToDictionary(u => u, u => new HashSet<string>(StringComparer.Ordinal));
        var commonNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups.OrderBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            var members = g.Where(x => moving.Contains((x.Unit, x.Name))).ToList();
            if (members.Count < 2) continue;
            if (!commonNames.Add(g.Key.Name))
            {
                // Same simple name, a second identical-body group: it stays per unit (common can hold one Name).
                foreach (var m in members) moving.Remove((m.Unit, m.Name));
                continue;
            }
            foreach (var m in members) movedByUnit[m.Unit].Add(m.Name);
            var first = members[0];
            var body = rootClasses[first.Unit][first.Name];
            foreach (var (from, to) in first.Unit.PackageMap.OrderByDescending(p => p.Key.Length))
                body = ReplacePackage(body, from, from == first.Unit.OrigRoot ? commonPkg : to);
            output[MainJava + commonPkg.Replace('.', '/') + "/" + first.Name + ".java"] = Normalise(body);
            shared.Add(new SharedItem("class", first.Name, commonPkg, members.Select(m => m.Unit.Source.UnitName).ToList()));
        }
        // Same simple name, different bodies: each unit keeps its own — worth knowing, not an error.
        foreach (var byName in parsed.SelectMany(u => rootClasses[u].Keys.Where(n => !movedByUnit[u].Contains(n)).Select(n => (u, n)))
                     .GroupBy(x => x.n).Where(g => g.Count() >= 2))
            conflicts.Add(new AssemblyConflict("class", byName.Key,
                $"{byName.Count()} units define their own {byName.Key} with different contents; each keeps its own in its package.",
                byName.Select(x => x.u.Source.UnitName).ToList()));

        // ── 3. Java sources and tests: re-packaged, application classes turned into configurations ──
        var beanNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fileCounts = new Dictionary<Unit, int>();
        foreach (var u in parsed)
        {
            var count = 0;
            foreach (var (path, raw) in u.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                if (!path.EndsWith(".java", StringComparison.Ordinal)) continue;
                var isTest = path.StartsWith(TestJava, StringComparison.Ordinal);
                if (!isTest && !path.StartsWith(MainJava, StringComparison.Ordinal)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                var pkg = PackageOf(raw);
                if (!isTest && pkg == u.OrigRoot && movedByUnit[u].Contains(name)) continue;

                var text = raw;
                foreach (var (from, to) in u.PackageMap.OrderByDescending(p => p.Key.Length)) text = ReplacePackage(text, from, to);
                foreach (var moved in movedByUnit[u])
                    text = Regex.Replace(text, $@"\b{Regex.Escape(u.NewRoot)}\.{Regex.Escape(moved)}\b", commonPkg + "." + moved);
                var newPkg = PackageOf(text);
                if (newPkg == u.NewRoot)
                {
                    var needs = movedByUnit[u].Where(m => m != name && Regex.IsMatch(StripComments(text), $@"\b{Regex.Escape(m)}\b")).OrderBy(m => m, StringComparer.Ordinal);
                    text = AddImports(text, needs.Select(m => commonPkg + "." + m));
                }

                if (!isTest && name == u.AppSimpleName && pkg == u.OrigRoot)
                {
                    text = SpringBootApp.Replace(text, "@org.springframework.context.annotation.Configuration(proxyBeanMethods = false)", 1);
                    text = RemoveMainMethod(text);
                }
                if (!isTest) text = AdviceNoArgs.Replace(text, m => $"@{m.Groups[1].Value}(basePackages = \"{u.NewRoot}\")");
                if (!isTest && (text.Contains("@Configuration") || name == u.AppSimpleName))
                    foreach (Match bm in BeanMethod.Matches(StripComments(text)))
                    {
                        if (!beanNames.TryGetValue(bm.Groups[1].Value, out var l)) beanNames[bm.Groups[1].Value] = l = new List<string>();
                        if (!l.Contains(u.Source.UnitName)) l.Add(u.Source.UnitName);
                    }
                if (isTest && u.AppSimpleName is { } app)
                {
                    // A unit's tests start only that unit (its own test application below), as they did when they
                    // passed alone — not the whole module, where another unit's beans, settings or tables can break them.
                    var unitTestApp = u.NewRoot + "." + UnitTestAppName;
                    var before = text;
                    text = Regex.Replace(text, $@"\b{Regex.Escape(app)}\.class\b", UnitTestAppName + ".class");
                    if (before != text && PackageOf(text) != u.NewRoot) text = AddImports(text, new[] { unitTestApp });
                }

                var target = (isTest ? TestJava : MainJava) + newPkg.Replace('.', '/') + "/" + name + ".java";
                output[target] = Normalise(text);
                count++;
            }
            fileCounts[u] = count;
        }
        var clashingBeans = beanNames.Where(b => b.Value.Count > 1).ToList();
        foreach (var b in clashingBeans)
            conflicts.Add(new AssemblyConflict("bean", b.Key,
                $"@Bean method {b.Key}() is declared by {b.Value.Count} units; the later definition replaces the earlier one (bean overriding is enabled for this).",
                b.Value));

        // ── 4. The one application class ──
        output[MainJava + basePkg.Replace('.', '/') + "/" + appSimple + ".java"] = Normalise($$"""
            package {{basePkg}};

            import org.springframework.boot.SpringApplication;
            import org.springframework.boot.autoconfigure.SpringBootApplication;
            import org.springframework.context.annotation.FullyQualifiedAnnotationBeanNameGenerator;

            /**
             * {{options.ModuleName}}: one application assembled by Astra from {{parsed.Count}} code unit{{(parsed.Count == 1 ? "" : "s")}}.
             * Each unit's code is in its own package below this one; bean names are fully qualified so units that both
             * declare, say, a CustomerService do not collide.
             */
            @SpringBootApplication(nameGenerator = FullyQualifiedAnnotationBeanNameGenerator.class)
            public class {{appSimple}} {
                public static void main(String[] args) {
                    SpringApplication.run({{appSimple}}.class, args);
                }
            }
            """);

        // ── 4b. One test application per unit: Spring finds the nearest @SpringBootConfiguration above a test's
        //        package, so each unit's tests (full contexts and slices alike) see that unit plus the shared classes.
        foreach (var u in parsed)
        {
            var scan = new[] { u.NewRoot, commonPkg }.Distinct(StringComparer.Ordinal).Select(p => $"\"{p}\"");
            output[TestJava + u.NewRoot.Replace('.', '/') + "/" + UnitTestAppName + ".java"] = Normalise($$"""
                package {{u.NewRoot}};

                import org.springframework.boot.autoconfigure.SpringBootApplication;
                import org.springframework.context.annotation.FullyQualifiedAnnotationBeanNameGenerator;

                /**
                 * Test-only: starts the code unit {{u.Source.UnitName}} on its own inside the assembled module, the way its tests ran
                 * when it passed alone. The module's application ({{appFqn}}) still holds every unit.
                 */
                @SpringBootApplication(scanBasePackages = { {{string.Join(", ", scan)}} }, nameGenerator = FullyQualifiedAnnotationBeanNameGenerator.class)
                public class {{UnitTestAppName}} {
                }
                """);
        }

        // ── 5. Resources: properties, schema and data merged; anything else kept once per path ──
        foreach (var root in new[] { MainRes, TestRes })
        {
            var props = new List<(string Unit, string Text)>();
            var schemas = new List<(string Unit, string Text)>();
            var data = new List<(string Unit, string Text)>();
            var others = new Dictionary<string, (string Unit, string Text)>(StringComparer.Ordinal);
            foreach (var u in parsed)
                foreach (var (path, text) in u.Files.Where(f => f.Key.StartsWith(root, StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    var rel = path[root.Length..];
                    if (rel == "application.properties") props.Add((u.Source.UnitName, text));
                    else if (rel == "schema.sql") schemas.Add((u.Source.UnitName, text));
                    else if (rel == "data.sql") data.Add((u.Source.UnitName, text));
                    else if (others.TryGetValue(rel, out var existing))
                    {
                        if (existing.Text != text)
                            conflicts.Add(new AssemblyConflict("resource", rel, $"{rel} differs between units; the first one is kept.", new[] { existing.Unit, u.Source.UnitName }));
                    }
                    else others[rel] = (u.Source.UnitName, text);
                }
            string? mergedSchema = schemas.Count > 0 ? SchemaMerger.Merge(schemas, conflicts, shared, notes) : null;
            if (props.Count > 0 || (root == MainRes))
            {
                var extra = new List<KeyValuePair<string, string>> { new("spring.application.name", artifactId) };
                if (root == MainRes && clashingBeans.Count > 0) extra.Add(new("spring.main.allow-bean-definition-overriding", "true"));
                // A unit whose own tests never started the database could pass alone with a column named like an H2
                // keyword (value, key, user …); merged, every context runs the whole schema. When no unit chose its
                // own datasource, the embedded database is told to accept exactly those words — still one fresh
                // database per test context (random name), as Spring gives each context without a url.
                // From the units' own schemas: the merged one already writes these names quoted.
                var reserved = H2Keywords.UsedAsNames(string.Join(";\n", schemas.Select(s => s.Text)));
                var unitsSetUrl = props.Any(p => Regex.IsMatch(p.Text, @"^\s*spring\.datasource\.url\s*[=:]", RegexOptions.Multiline));
                if (root == MainRes && reserved.Count > 0 && !unitsSetUrl)
                {
                    extra.Add(new("spring.datasource.url", $"jdbc:h2:mem:${{random.uuid}};NON_KEYWORDS={string.Join(",", reserved)}"));
                    notes.Add($"Columns named {string.Join(", ", reserved.Select(r => r.ToLowerInvariant()))} are H2 keywords; the embedded database is told to accept them as names (NON_KEYWORDS).");
                }
                output[root + "application.properties"] = PropertiesMerger.Merge(props, extra, conflicts);
            }
            if (mergedSchema is not null) output[root + "schema.sql"] = mergedSchema;
            if (data.Count > 0) output[root + "data.sql"] = SchemaMerger.MergeStatements(data, "data.sql");
            foreach (var (rel, v) in others) output[root + rel] = v.Text;
        }

        // ── 6. The pom ──
        output["pom.xml"] = PomMerger.Merge(parsed.Select(u => (u.Source.UnitName, u.Files.GetValueOrDefault("pom.xml"))).ToList(),
            "com.astra.assembled", artifactId, options.ModuleName, conflicts, notes);

        // ── 7. Anything else a unit carried (README, Dockerfile …) is kept under units/<segment>/ for reference ──
        foreach (var u in parsed)
            foreach (var (path, text) in u.Files)
            {
                if (path == "pom.xml" || path.StartsWith("src/", StringComparison.Ordinal)) continue;
                output["units/" + u.Seg + "/" + path] = text;
            }

        var assembled = parsed.Select(u => new AssembledUnit(u.Source.UnitId, u.Source.UnitName, u.Source.SourcePath, u.Source.ScaffoldId,
            u.NewRoot, fileCounts.GetValueOrDefault(u))).ToList();
        if (parsed.Any(u => u.OrigRoot.Length == 0)) notes.Add("Some units had no Java package declaration; their files were placed in a package named after the unit.");
        var report = new AssemblyReport(artifactId, basePkg, appFqn, assembled, shared, conflicts, notes);
        output["ASSEMBLY.md"] = ReportMarkdown(options.ModuleName, report);
        return new AssembledModule(output, report);
    }

    // ── helpers ──

    /// <summary>Package files rooted at their pom: a package may hold its Maven project under a folder (backend/).</summary>
    private static Dictionary<string, string> Rooted(IReadOnlyDictionary<string, string> files)
    {
        var norm = files.ToDictionary(f => f.Key.Replace('\\', '/').TrimStart('/'), f => f.Value, StringComparer.Ordinal);
        var pom = norm.Keys.Where(k => k == "pom.xml" || k.EndsWith("/pom.xml", StringComparison.Ordinal)).OrderBy(k => k.Length).FirstOrDefault();
        if (pom is null || pom == "pom.xml") return norm;
        var prefix = pom[..^"pom.xml".Length];
        return norm.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(f => f.Key[prefix.Length..], f => f.Value, StringComparer.Ordinal);
    }

    internal static string PackageOf(string java)
    {
        var m = PackageDecl.Match(java);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Every reference to a package (declaration, imports, qualified names, string literals) moved to another.</summary>
    internal static string ReplacePackage(string text, string from, string to) =>
        from.Length == 0 ? text : Regex.Replace(text, $@"(?<![\w.]){Regex.Escape(from)}(?=[.;\s*""])", to);

    internal static string AddImports(string java, IEnumerable<string> fqns)
    {
        var lines = fqns.Where(f => !Regex.IsMatch(java, $@"^\s*import\s+{Regex.Escape(f)}\s*;", RegexOptions.Multiline))
            .Select(f => $"import {f};").ToList();
        if (lines.Count == 0) return java;
        var m = PackageDecl.Match(java);
        if (!m.Success) return string.Join("\n", lines) + "\n" + java;
        var at = m.Index + m.Length;
        return java[..at] + "\n\n" + string.Join("\n", lines) + java[at..];
    }

    /// <summary>Removes <c>public static void main(String[] args) { … }</c>, braces matched.</summary>
    internal static string RemoveMainMethod(string java)
    {
        var m = Regex.Match(java, @"(?:public\s+)?static\s+void\s+main\s*\([^)]*\)\s*(throws\s+[\w.,\s]+)?\{");
        if (!m.Success) return java;
        var depth = 0;
        for (var i = m.Index + m.Length - 1; i < java.Length; i++)
        {
            if (java[i] == '{') depth++;
            else if (java[i] == '}' && --depth == 0)
            {
                var start = m.Index;
                // take the method's leading indentation and any blank line it leaves behind
                while (start > 0 && (java[start - 1] == ' ' || java[start - 1] == '\t')) start--;
                var end = i + 1;
                if (end < java.Length && java[end] == '\r') end++;
                if (end < java.Length && java[end] == '\n') end++;
                return java[..start] + java[end..];
            }
        }
        return java;
    }

    private static string StripComments(string java) =>
        Regex.Replace(java, @"/\*.*?\*/|//[^\n]*", " ", RegexOptions.Singleline);

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd() + "\n";

    /// <summary>A valid, lowercase Java package segment.</summary>
    internal static string Segment(string raw)
    {
        var s = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9_]", "_").Trim('_');
        if (s.Length == 0) s = "unit";
        if (char.IsDigit(s[0])) s = "u" + s;
        if (JavaKeywords.Contains(s)) s = "u" + s;
        return s;
    }

    private static string Pascal(string slug)
    {
        var p = string.Concat(slug.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        if (p.Length == 0 || char.IsDigit(p[0])) p = "Module" + p;
        return p;
    }

    private static string ReportMarkdown(string moduleName, AssemblyReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {moduleName}");
        sb.AppendLine();
        sb.AppendLine($"One Spring Boot application (`{r.ApplicationClass}`, artifact `{r.ArtifactId}`) assembled by Astra from {r.Units.Count} code unit(s).");
        sb.AppendLine();
        sb.AppendLine("## Code units");
        foreach (var u in r.Units) sb.AppendLine($"- `{u.UnitName}` ({u.SourcePath}) → `{u.Package}`");
        if (r.Shared.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Held once");
            foreach (var s in r.Shared) sb.AppendLine($"- {s.Kind} `{s.Name}` in `{s.Location}` (from {string.Join(", ", s.Units)})");
        }
        if (r.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Disagreements");
            foreach (var c in r.Conflicts) sb.AppendLine($"- {c.Kind} `{c.Name}`: {c.Detail} ({string.Join(", ", c.Units)})");
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            foreach (var n in r.Notes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }
}
