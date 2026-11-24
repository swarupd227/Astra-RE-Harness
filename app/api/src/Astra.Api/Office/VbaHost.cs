using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Office;

/// <summary>
/// What a workbook's VBA project adds to a reading of the workbook itself, independent of file format:
/// which routine a macro name points at, what runs without a button (events), what each module names in
/// the workbook (sheets, named cells, addresses), and which sheets and names a formula's text reads.
/// Shared by the .xlsx/.xlsm reader (<see cref="WorkbookSurface"/>) and the legacy .xls reader.
/// </summary>
internal sealed class VbaHost
{
    private readonly VbaProject? _vba;
    private readonly string _file;
    public Dictionary<string, List<string>> Routines { get; }

    public VbaHost(VbaProject? vba, string file)
    {
        _vba = vba; _file = file;
        Routines = (vba?.Modules ?? Array.Empty<VbaModule>())
            .ToDictionary(m => m.Name, m => RoutineNames(m.Source), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary><c>[0]!Module.Routine</c>, <c>'Book.xlsm'!Routine</c> or a bare routine → where it is in this project.</summary>
    public (string? Module, string? Routine, bool Found) ResolveMacro(string? macro)
    {
        if (string.IsNullOrWhiteSpace(macro)) return (null, null, false);
        var m = macro.Trim();
        var bang = m.LastIndexOf('!');
        var external = false;
        if (bang >= 0)
        {
            var book = m[..bang];
            external = !(book == "[0]" || book.Length == 0 || book.Contains(_file, StringComparison.OrdinalIgnoreCase));
            m = m[(bang + 1)..];
        }
        if (external) return (null, m, false);
        var dot = m.LastIndexOf('.');
        if (dot > 0)
        {
            var mod = m[..dot]; var routine = m[(dot + 1)..];
            return (mod, routine, Routines.TryGetValue(mod, out var rs) && rs.Contains(routine, StringComparer.OrdinalIgnoreCase));
        }
        // Unqualified: Excel looks through the standard modules.
        var standard = _vba?.Modules.Where(x => x.Kind == VbaModuleKind.Standard).Select(x => x.Name) ?? Enumerable.Empty<string>();
        foreach (var mod in standard.Concat(Routines.Keys))
            if (Routines[mod].Contains(m, StringComparer.OrdinalIgnoreCase)) return (mod, m, true);
        return (null, m, false);
    }

    // ── Code ↔ workbook ────────────────────────────────────────────────

    private static readonly HashSet<string> WorkbookEvents = new(StringComparer.OrdinalIgnoreCase)
    { "Open", "BeforeClose", "BeforeSave", "AfterSave", "BeforePrint", "Activate", "Deactivate", "NewSheet",
      "SheetChange", "SheetActivate", "SheetCalculate", "SheetSelectionChange", "SheetBeforeDoubleClick", "SheetBeforeRightClick",
      "WindowActivate", "WindowResize", "AddinInstall", "AddinUninstall" };
    private static readonly HashSet<string> SheetEvents = new(StringComparer.OrdinalIgnoreCase)
    { "Change", "SelectionChange", "Activate", "Deactivate", "Calculate", "BeforeDoubleClick", "BeforeRightClick",
      "FollowHyperlink", "PivotTableUpdate" };

    public List<WorkbookEvent> ReadEvents(List<WorkbookSheet> sheets)
    {
        var list = new List<WorkbookEvent>();
        if (_vba is null) return list;
        var byCode = sheets.Where(s => s.CodeName is not null).ToDictionary(s => s.CodeName!, s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var m in _vba.Modules.Where(m => m.Kind == VbaModuleKind.Document))
            foreach (var r in Routines[m.Name])
            {
                var us = r.IndexOf('_');
                if (us <= 0) continue;
                var (prefix, ev) = (r[..us], r[(us + 1)..]);
                if (prefix.Equals("Workbook", StringComparison.OrdinalIgnoreCase) && WorkbookEvents.Contains(ev))
                    list.Add(new WorkbookEvent(m.Name, r, r, null));
                else if (prefix.Equals("Worksheet", StringComparison.OrdinalIgnoreCase) && SheetEvents.Contains(ev))
                    list.Add(new WorkbookEvent(m.Name, r, r, byCode.GetValueOrDefault(m.Name)));
            }
        // Auto_Open in a standard module is the older spelling of Workbook_Open.
        foreach (var m in _vba.Modules.Where(m => m.Kind == VbaModuleKind.Standard))
            foreach (var r in Routines[m.Name].Where(r => r.Equals("Auto_Open", StringComparison.OrdinalIgnoreCase) || r.Equals("Auto_Close", StringComparison.OrdinalIgnoreCase)))
                list.Add(new WorkbookEvent(m.Name, r, r, null));
        return list;
    }

    private static readonly Regex StringCallRe = new(@"\b(?<fn>Sheets|Worksheets|Range|Names)\s*\(\s*""(?<arg>[^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex BracketRe = new(@"(?<![\w\]""])\[(?<arg>[A-Za-z_][\w.]*|\$?[A-Z]{1,3}\$?\d+(?::\$?[A-Z]{1,3}\$?\d+)?)\]");
    private static readonly Regex CellAddrRe = new(@"^\$?[A-Z]{1,3}\$?\d+(?::\$?[A-Z]{1,3}\$?\d+)?$", RegexOptions.IgnoreCase);
    private static readonly Regex IdentRe = new(@"\b[A-Za-z_]\w*\b");

    public List<WorkbookCodeReach> ReadCodeReach(List<WorkbookSheet> sheets, List<WorkbookName> names)
    {
        var list = new List<WorkbookCodeReach>();
        if (_vba is null) return list;
        var sheetByName = sheets.ToDictionary(s => s.Name, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var sheetByCode = sheets.Where(s => s.CodeName is not null).ToDictionary(s => s.CodeName!, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var nameSet = new HashSet<string>(names.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var m in _vba.Modules)
        {
            var code = StripCommentsAndAttributes(m.Source);
            var s = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var n = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var c = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match x in StringCallRe.Matches(code))
            {
                var arg = x.Groups["arg"].Value;
                var fn = x.Groups["fn"].Value;
                if (fn.EndsWith("heets", StringComparison.OrdinalIgnoreCase)) { if (sheetByName.TryGetValue(arg, out var sn)) s.Add(sn); }
                else if (nameSet.Contains(arg)) n.Add(arg);
                else if (CellAddrRe.IsMatch(arg)) c.Add(arg.Replace("$", "").ToUpperInvariant());
            }
            foreach (Match x in BracketRe.Matches(code))
            {
                var arg = x.Groups["arg"].Value;
                if (nameSet.Contains(arg)) n.Add(arg);
                else if (CellAddrRe.IsMatch(arg)) c.Add(arg.Replace("$", "").ToUpperInvariant());
            }
            // A sheet's code name used as an object (Result.Range(...)) — but not the module itself.
            foreach (Match x in IdentRe.Matches(StripStrings(code)))
                if (sheetByCode.TryGetValue(x.Value, out var sn) && !x.Value.Equals(m.Name, StringComparison.OrdinalIgnoreCase)) s.Add(sn);
            if (s.Count + n.Count + c.Count > 0) list.Add(new WorkbookCodeReach(m.Name, s.ToList(), n.ToList(), c.ToList()));
        }
        return list;
    }

    // ── Formulas ──────────────────────────────────────────────────────

    private static readonly Regex QuotedSheetRefRe = new(@"'((?:[^']|'')+)'!");
    private static readonly Regex PlainSheetRefRe = new(@"(?<![\w.'\]])([A-Za-z_][\w.]*)!");
    private static readonly Regex ExternalRefRe = new(@"\[(\d+)\]");
    private static readonly Regex TableRefRe = new(@"\b([A-Za-z_][\w.]*)\[");

    public static List<string> FormulaRefs(string formula, string self, List<string> sheetNames, HashSet<string> nameSet, HashSet<string> tables)
    {
        var refs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var noStrings = StripFormulaStrings(formula);
        foreach (Match m in QuotedSheetRefRe.Matches(noStrings)) AddSheet(m.Groups[1].Value.Replace("''", "'"));
        foreach (Match m in PlainSheetRefRe.Matches(noStrings)) AddSheet(m.Groups[1].Value);
        if (ExternalRefRe.IsMatch(noStrings)) refs.Add("external");
        foreach (Match m in IdentRe.Matches(QuotedSheetRefRe.Replace(noStrings, "")))
            if (nameSet.Contains(m.Value)) refs.Add("name:" + m.Value);
        foreach (Match m in TableRefRe.Matches(noStrings))
            if (tables.Contains(m.Groups[1].Value)) refs.Add("table:" + m.Groups[1].Value);
        return refs.ToList();

        void AddSheet(string s)
        {
            var bracket = s.LastIndexOf(']');
            if (bracket >= 0) { refs.Add("external"); return; }
            if (!s.Equals(self, StringComparison.OrdinalIgnoreCase) && sheetNames.Contains(s, StringComparer.OrdinalIgnoreCase)) refs.Add("sheet:" + s);
        }
    }

    private static string StripFormulaStrings(string f) => Regex.Replace(f, "\"(?:[^\"]|\"\")*\"", "\"\"");
    private static string StripStrings(string code) => Regex.Replace(code, "\"[^\"\\n]*\"", "\"\"");

    private static string StripCommentsAndAttributes(string src)
    {
        var sb = new StringBuilder(src.Length);
        foreach (var raw in src.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("Attribute ", StringComparison.Ordinal)) continue;
            var inStr = false; var cut = raw.Length;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '"') inStr = !inStr;
                else if (raw[i] == '\'' && !inStr) { cut = i; break; }
            }
            sb.Append(raw, 0, cut).Append('\n');
        }
        return sb.ToString();
    }

    private static readonly Regex RoutineRe = new(
        @"^[ \t]*(?:(?:Public|Private|Friend)[ \t]+)?(?:Static[ \t]+)?(?:Sub|Function|Property[ \t]+(?:Get|Let|Set))[ \t]+(\w+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static List<string> RoutineNames(string source) =>
        RoutineRe.Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
