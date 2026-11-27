using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>
/// Reads the facts of a VB6 project's files (.frm forms, .cls classes, .bas modules, .ctl user controls): the
/// screens and what is on them, the external libraries and ActiveX controls each file depends on, which screens a
/// file opens, the tables its embedded SQL reads and writes, the settings (registry and INI) and the files it reads
/// and writes. Pattern-based over the text — VB6 keeps a form's layout in a fixed header and its SQL in string
/// literals, both regular enough to read without running anything. Pure: text in, facts out; every fact points at
/// the line that shows it. SQL assembled from variables is read from its literal parts, so its links are medium
/// confidence.
/// </summary>
internal static class Vb6Facts
{
    public const string ScreenType = "screen";
    public const string LibraryType = "library";
    public const string ModuleType = "module";
    public const string Opens = "opens";

    public const int MaxFileChars = 8_500_000;

    public static bool IsVb6(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".frm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cls", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bas", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ctl", StringComparison.OrdinalIgnoreCase);
    }

    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly Regex VbName = new(@"^Attribute\s+VB_Name\s*=\s*""([^""]+)""", Opt);
    private static readonly Regex ObjectLine = new(@"^Object\s*=\s*""?\{[^}]+\}[^;]*;\s*""?([^""\r\n]+?)""?\s*$", Opt);
    private static readonly Regex BeginControl = new(@"^\s*Begin\s+([\w.]+)\s+(\w+)\s*$", Opt);
    private static readonly Regex CaptionLine = new(@"^\s*Caption\s*=\s*""((?:[^""]|"""")*)""", Opt);
    private static readonly Regex DeclareLib = new(@"\bDeclare\s+(?:PtrSafe\s+)?(?:Function|Sub)\s+(\w+)\s+Lib\s+""([^""]+)""", Opt);
    private static readonly Regex CreateObject = new(@"\bCreateObject\s*\(\s*""([^""]+)""", Opt);
    private static readonly Regex Show = new(@"\b([A-Za-z_]\w*)\s*\.\s*Show\b", Opt);
    private static readonly Regex GetSetting = new(@"\b(GetSetting|GetAllSettings|SaveSetting|DeleteSetting)\s*\(?\s*([^,]+),\s*([^,]+),\s*([^,)\r\n]+)", Opt);
    private static readonly Regex Profile = new(@"\b(GetPrivateProfileString|GetPrivateProfileInt|WritePrivateProfileString)\w*\s*\(\s*([^,]+),\s*([^,]+),", Opt);
    private static readonly Regex OpenFile = new(@"^\s*Open\s+(.+?)\s+For\s+(Input|Output|Append|Binary|Random)\b", Opt);
    private static readonly Regex StringLiteral = new(@"""((?:[^""]|"""")*)""", RegexOptions.Compiled);
    /// <summary>A literal that is SQL by its shape, not one that merely starts with an English word such as "Update".</summary>
    private static readonly Regex SqlStart = new(
        @"^\s*(SELECT\s+(DISTINCT\s+|TOP\s+\d+\s+)?[\w*\[\]\.,\s()]+?\s+FROM\s+[\[\w]|INSERT\s+INTO\s+[\[\w][\w\].]*\s*(\(|VALUES|SELECT)|UPDATE\s+[\[\w][\w\].]*\s+SET\s+\w|DELETE\s+(\*\s+)?FROM\s+[\[\w])", Opt);
    private static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    /// <summary>Names that end in <c>.Show</c> without being a screen: dialogs, the Me form, common-dialog methods.</summary>
    private static readonly HashSet<string> NotScreens = new(StringComparer.OrdinalIgnoreCase) { "Me", "MsgBox", "Err", "App", "Screen", "Printer" };

    public static IReadOnlyDictionary<string, CodeFileFacts> Read(IReadOnlyList<CodeSource> files, CancellationToken ct = default)
    {
        var result = new Dictionary<string, CodeFileFacts>(StringComparer.Ordinal);
        // A name is a screen when some form says so; only those count as screens another file opens.
        var screens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.Where(f => f.Path.EndsWith(".frm", StringComparison.OrdinalIgnoreCase) && f.Text.Length <= MaxFileChars))
            if (NameOf(f.Text) is { } n) screens.Add(n);

        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Text.Length > MaxFileChars)
            {
                result[f.Path] = new(FactSet.Empty, new[] { $"It is longer than {MaxFileChars:N0} characters, so it was not read for facts." });
                continue;
            }
            try { result[f.Path] = ReadFile(f, screens); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                result[f.Path] = new(FactSet.Empty, new[] { $"It could not be read for facts ({ex.GetType().Name})." });
            }
        }
        return result;
    }

    private static string? NameOf(string text)
    {
        foreach (var line in text.Split('\n').Take(400))
        {
            var m = VbName.Match(line.TrimEnd('\r'));
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static CodeFileFacts ReadFile(CodeSource file, IReadOnlySet<string> screens)
    {
        var lines = file.Text.Replace("\r\n", "\n").Split('\n');
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
        var name = NameOf(file.Text) ?? System.IO.Path.GetFileNameWithoutExtension(file.Path);
        var selfType = ext switch { ".frm" => ScreenType, ".cls" => EntityVocabulary.Class, ".ctl" => EntityVocabulary.Class, _ => ModuleType };
        var self = new EntityRefFact(selfType, name);
        var entities = new List<EntityFact>();
        var links = new List<LinkFact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Link(string relation, EntityRefFact to, int line, string confidence = EntityVocabulary.High, IReadOnlyDictionary<string, object?>? attrs = null)
        {
            if (!seen.Add($"{relation}|{to.Type}|{to.Name}")) return;
            links.Add(new LinkFact(relation, self, to, EvidenceLocator.Lines(line, line), confidence, attrs));
            // A library is stated by the file that depends on it: the project has no other definition of it.
            if (to.Type == LibraryType && seen.Add($"entity|{to.Name}"))
                entities.Add(new EntityFact(LibraryType, to.Name, EvidenceLocator.Lines(line, line),
                    Attrs.Of(("kind", attrs is not null && attrs.TryGetValue("kind", out var k) ? k : null))));
        }

        // ── the header: the form's layout (forms and user controls) ──
        var codeStart = 0;
        var controls = new List<(string Kind, string Name)>();
        string? caption = null;
        var headerEnd = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("Attribute VB_", StringComparison.OrdinalIgnoreCase)) { codeStart = i + 1; continue; }
            if (codeStart > 0) break;
            var obj = ObjectLine.Match(line);
            if (obj.Success)
            {
                var lib = obj.Groups[1].Value.Trim();
                Link(EntityVocabulary.References, new EntityRefFact(LibraryType, lib), i + 1, attrs: Attrs.Of(("kind", "activex-control")));
                continue;
            }
            var begin = BeginControl.Match(line);
            if (begin.Success)
            {
                if (begin.Groups[1].Value.Equals("VB.Form", StringComparison.OrdinalIgnoreCase)
                    || begin.Groups[1].Value.Equals("VB.UserControl", StringComparison.OrdinalIgnoreCase)
                    || begin.Groups[1].Value.Equals("VB.MDIForm", StringComparison.OrdinalIgnoreCase))
                    headerEnd = i + 1;
                else controls.Add((begin.Groups[1].Value, begin.Groups[2].Value));
                continue;
            }
            if (caption is null && controls.Count == 0 && headerEnd > 0 && CaptionLine.Match(line) is { Success: true } cap)
                caption = cap.Groups[1].Value.Replace("\"\"", "\"");
        }

        if (selfType == ScreenType || ext == ".ctl")
        {
            var kinds = controls.GroupBy(c => c.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} ×{g.Count()}").ToList();
            entities.Add(new EntityFact(selfType, name, EvidenceLocator.Lines(Math.Max(1, headerEnd), Math.Max(1, codeStart)),
                Attrs.Of(("caption", caption), ("controls", controls.Count), ("controlKinds", kinds.Take(12).ToList()), ("file", file.Path))));
        }
        else
        {
            entities.Add(new EntityFact(selfType, name, EvidenceLocator.Lines(1, Math.Max(1, codeStart)), Attrs.Of(("file", file.Path))));
        }

        // ── the code ──
        for (var i = Math.Max(0, codeStart); i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var code = StripComment(raw);
            if (code.Trim().Length == 0) continue;
            var lineNo = i + 1;

            // A statement continued with " _" is read as one (SQL is often built that way).
            var statement = code;
            var j = i;
            while (statement.TrimEnd().EndsWith(" _", StringComparison.Ordinal) && j + 1 < lines.Length && j - i < 40)
            {
                j++;
                statement = statement.TrimEnd()[..^1] + " " + StripComment(lines[j].TrimEnd('\r'));
            }

            foreach (Match m in DeclareLib.Matches(statement))
                Link(EntityVocabulary.References, new EntityRefFact(LibraryType, NormaliseLib(m.Groups[2].Value)), lineNo,
                    attrs: Attrs.Of(("kind", "dll"), ("function", m.Groups[1].Value)));
            // A declaration or a routine's own header is a signature, not a use.
            if (Regex.IsMatch(statement, @"\bDeclare\s+(PtrSafe\s+)?(Function|Sub)\b|^\s*((Public|Private|Friend)\s+)?(Static\s+)?(Function|Sub|Property\s+(Get|Let|Set))\s", Opt)) { i = j; continue; }
            foreach (Match m in CreateObject.Matches(statement))
                Link(EntityVocabulary.References, new EntityRefFact(LibraryType, m.Groups[1].Value), lineNo, attrs: Attrs.Of(("kind", "com-progid")));
            foreach (Match m in Show.Matches(statement))
            {
                var target = m.Groups[1].Value;
                if (NotScreens.Contains(target) || !screens.Contains(target) || target.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                Link(Opens, new EntityRefFact(ScreenType, target), lineNo, attrs: Attrs.Of(("modal", Regex.IsMatch(statement, @"\bvbModal\b|\.Show\s*\(?\s*1\b", Opt))));
            }
            foreach (Match m in GetSetting.Matches(statement))
            {
                var key = $"{Literal(m.Groups[2].Value)}/{Literal(m.Groups[3].Value)}/{Literal(m.Groups[4].Value)}";
                var writes = m.Groups[1].Value.StartsWith("Save", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value.StartsWith("Delete", StringComparison.OrdinalIgnoreCase);
                Link(writes ? EntityVocabulary.Writes : EntityVocabulary.Reads, new EntityRefFact(EntityVocabulary.ConfigKey, "registry:" + key), lineNo,
                    KeyIsLiteral(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value) ? EntityVocabulary.High : EntityVocabulary.Medium);
            }
            foreach (Match m in Profile.Matches(statement))
            {
                var key = $"ini:{Literal(m.Groups[2].Value)}/{Literal(m.Groups[3].Value)}";
                var writes = m.Groups[1].Value.StartsWith("Write", StringComparison.OrdinalIgnoreCase);
                Link(writes ? EntityVocabulary.Writes : EntityVocabulary.Reads, new EntityRefFact(EntityVocabulary.ConfigKey, key), lineNo,
                    KeyIsLiteral(m.Groups[2].Value, m.Groups[3].Value) ? EntityVocabulary.High : EntityVocabulary.Medium);
            }
            if (OpenFile.Match(statement) is { Success: true } open)
            {
                var literals = StringLiteral.Matches(open.Groups[1].Value).Select(x => x.Groups[1].Value).Where(x => x.Trim().Length > 0).ToList();
                var fileName = literals.Count > 0 ? literals[^1].Trim().TrimStart('\\', '/') : null;
                if (fileName is { Length: > 0 })
                {
                    var mode = open.Groups[2].Value.ToLowerInvariant();
                    Link(mode is "input" ? EntityVocabulary.Reads : mode is "binary" or "random" ? EntityVocabulary.Reads : EntityVocabulary.Writes,
                        new EntityRefFact(EntityVocabulary.File, fileName), lineNo,
                        literals.Count == 1 && !open.Groups[1].Value.Contains('&') ? EntityVocabulary.High : EntityVocabulary.Medium,
                        Attrs.Of(("mode", mode)));
                }
            }

            // Embedded SQL: the literal parts of the statement, read as one.
            var sql = string.Concat(StringLiteral.Matches(statement).Select(x => x.Groups[1].Value.Replace("\"\"", "\"") + " "));
            if (sql.Length > 10 && SqlStart.IsMatch(sql))
            {
                var o = SqlPatternReader.ScanBody(sql);
                foreach (var t in o.Reads.Where(IsTableName))
                    Link(EntityVocabulary.Reads, new EntityRefFact(EntityVocabulary.Table, SqlPatternReader.Normalise(t)), lineNo, EntityVocabulary.Medium);
                foreach (var t in o.Writes.Where(IsTableName))
                    Link(EntityVocabulary.Writes, new EntityRefFact(EntityVocabulary.Table, SqlPatternReader.Normalise(t)), lineNo, EntityVocabulary.Medium);
            }
            i = j;
        }
        return new CodeFileFacts(new FactSet(entities, links), Array.Empty<string>());
    }

    /// <summary>A table name, not a fragment left where a variable was concatenated in.</summary>
    private static bool IsTableName(string t)
    {
        var n = SqlPatternReader.Normalise(t);
        return n.Length > 1 && Identifier.IsMatch(n.Replace(".", "_")) && !n.Equals("WHERE", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseLib(string lib)
    {
        var l = lib.Trim();
        return l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? l : l + ".dll";
    }

    private static bool KeyIsLiteral(params string[] parts) =>
        parts.All(p => Regex.Replace(p.Trim(), @"^\w+\s*:=\s*", "").StartsWith('"'));

    /// <summary>A string literal's text, or the expression itself (a variable or constant) in braces.</summary>
    private static string Literal(string expr)
    {
        var e = Regex.Replace(expr.Trim(), @"^\w+\s*:=\s*", "");   // a named argument: AppName:="X"
        var m = StringLiteral.Match(e);
        return m.Success && m.Index == 0 && m.Length == e.Length ? m.Groups[1].Value.Replace("\"\"", "\"") : "{" + e + "}";
    }

    /// <summary>Drops a trailing ' comment or a Rem line; quotes inside strings are left alone.</summary>
    internal static string StripComment(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("'", StringComparison.Ordinal) || t.StartsWith("Rem ", StringComparison.OrdinalIgnoreCase)) return "";
        var inStr = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inStr = !inStr;
            else if (line[i] == '\'' && !inStr) return line[..i];
        }
        return line;
    }
}
