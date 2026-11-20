using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Office;

// ─────────────────────────────────────────────────────────────────────────────
// What a workbook holds besides its macros: the sheets, the names that point
// into them, the formulas, the drop-down lists, the buttons and boxes on the
// sheets and which macro each one runs, and the events the code answers. A
// template's logic is split between these and the VBA; a migration that reads
// only the code misses the rating tables it reads and the cells it writes.
// Read statically from the package XML — nothing is calculated or run.
// ─────────────────────────────────────────────────────────────────────────────

/// <param name="State">"visible", "hidden" or "veryHidden" (hidden from the Excel UI, reachable only from code).</param>
/// <param name="CodeName">The sheet's name in VBA (<c>Sheet1</c>, or whatever the author set), which is how code
/// usually reaches it and which names the module holding its event handlers.</param>
public sealed record WorkbookSheet(
    string Name, string? CodeName, string State, string? UsedRange,
    int ValueCells, int FormulaCells,
    IReadOnlyList<WorkbookFormula> Formulas,
    IReadOnlyList<WorkbookValidation> Validations,
    IReadOnlyList<WorkbookControl> Controls,
    IReadOnlyList<WorkbookTable> Tables,
    IReadOnlyList<string> ReadsFromSheets);

/// <param name="Cell">The cell holding the formula; for a formula Excel shares across a range,
/// <paramref name="Range"/> is that range and the formula is written as it reads in its first cell.</param>
/// <param name="Refs">Other sheets and defined names the formula reads.</param>
public sealed record WorkbookFormula(string Cell, string Formula, string? Range, bool IsArray, IReadOnlyList<string> Refs);

/// <param name="Type">"list", "whole", "decimal", "date", "textLength", "custom", …</param>
/// <param name="Source">For a list, its items or the range it takes them from.</param>
/// <param name="Label">The text next to the first cell, which is usually what the input is called on the form.</param>
public sealed record WorkbookValidation(string Cells, string Type, string? Source, string? Label, string? Prompt);

/// <param name="Technology">"form" (Excel's own controls), "activex" or "shape" (a drawing with a macro assigned).</param>
/// <param name="Kind">Button, CheckBox, DropDown, CommandButton, TextBox, …</param>
/// <param name="Macro">The macro as Excel stores it (<c>[0]!Module.Routine</c>), for form controls and shapes.</param>
/// <param name="LinkedCell">The cell the control writes its value to.</param>
/// <param name="InputRange">For a list or drop-down, the range its items come from.</param>
/// <param name="Handlers">Routines in the sheet's module named for this control (<c>cmdStart_Click</c>) — how an ActiveX control runs code.</param>
public sealed record WorkbookControl(
    string Name, string Technology, string Kind, string? Text,
    string? Macro, string? MacroModule, string? MacroRoutine, bool MacroFound,
    string? LinkedCell, string? InputRange, IReadOnlyList<string> Handlers);

public sealed record WorkbookTable(string Name, string Range, IReadOnlyList<string> Columns);

/// <param name="Scope">The sheet a name is local to, or null for a workbook-wide name.</param>
/// <param name="Label">For a name pointing at one cell, the text beside that cell.</param>
public sealed record WorkbookName(string Name, string RefersTo, string? Scope, bool Hidden, string? Label);

/// <param name="Trigger">"Workbook_Open", "Worksheet_Change", … — something the user does that runs code without a button.</param>
public sealed record WorkbookEvent(string Module, string Routine, string Trigger, string? Sheet);

/// <param name="Sheets">Sheets the module names (<c>Sheets("Rates")</c>, or the sheet's code name).</param>
/// <param name="Names">Defined names the module reads or writes (<c>Range("Premium")</c>, <c>[Premium]</c>).</param>
/// <param name="Cells">Cell addresses the module names in <c>Range("…")</c>/<c>[…]</c>.</param>
public sealed record WorkbookCodeReach(string Module, IReadOnlyList<string> Sheets, IReadOnlyList<string> Names, IReadOnlyList<string> Cells);

/// <param name="Kind">"workbook" (a link to another file's cells) or the connection's type (odbc, oledb, web, text, …).</param>
/// <param name="Target">Where it points, with any password or key removed.</param>
public sealed record WorkbookExternal(string Kind, string Name, string? Target);

public sealed record WorkbookSurfaceResult(
    string FileName,
    IReadOnlyList<WorkbookSheet> Sheets,
    IReadOnlyList<WorkbookName> Names,
    IReadOnlyList<WorkbookEvent> Events,
    IReadOnlyList<WorkbookCodeReach> CodeReach,
    IReadOnlyList<WorkbookExternal> Externals,
    VbaProject? Vba,
    IReadOnlyList<string> Warnings);

public static class WorkbookSurface
{
    /// <summary>Formulas kept per sheet; a model sheet can hold tens of thousands, the count is still exact.</summary>
    public const int MaxFormulasPerSheet = 2000;
    /// <summary>A part claiming more than this is not inflated.</summary>
    public const long MaxPartBytes = 64L << 20;

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>ActiveX class ids of the Microsoft Forms 2.0 controls (FM20.DLL), as they appear in <c>activeX*.xml</c>.</summary>
    private static readonly Dictionary<string, string> ActiveXKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{D7053240-CE69-11CD-A777-00DD01143C57}"] = "CommandButton",
        ["{8BD21D10-EC42-11CE-9E0D-00AA006002F3}"] = "TextBox",
        ["{8BD21D20-EC42-11CE-9E0D-00AA006002F3}"] = "ListBox",
        ["{8BD21D30-EC42-11CE-9E0D-00AA006002F3}"] = "ComboBox",
        ["{8BD21D40-EC42-11CE-9E0D-00AA006002F3}"] = "CheckBox",
        ["{8BD21D50-EC42-11CE-9E0D-00AA006002F3}"] = "OptionButton",
        ["{8BD21D60-EC42-11CE-9E0D-00AA006002F3}"] = "ToggleButton",
        ["{978C9E23-D4B0-11CE-BF2D-00AA003F40D0}"] = "Label",
        ["{4C599241-6926-101B-9992-00000B65C6F9}"] = "Image",
        ["{DFD181E0-5E2F-11CE-A449-00AA004A803D}"] = "ScrollBar",
        ["{79176FB0-B7F2-11CE-97EF-00AA006D2776}"] = "SpinButton",
    };

    public static WorkbookSurfaceResult Read(byte[] file, string fileName)
    {
        var warnings = new List<string>();
        var vba = OfficeVba.Read(file, fileName);
        warnings.AddRange(vba.Warnings);
        var empty = new WorkbookSurfaceResult(fileName, Array.Empty<WorkbookSheet>(), Array.Empty<WorkbookName>(),
            Array.Empty<WorkbookEvent>(), Array.Empty<WorkbookCodeReach>(), Array.Empty<WorkbookExternal>(), vba.Project, warnings);

        if (vba.Container == "ole")
        {
            warnings.Add($"{fileName} is a legacy binary workbook: its VBA was read, but its sheets, names and controls are not read yet.");
            return empty;
        }
        if (vba.Container != "ooxml") return empty;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
            if (zip.GetEntry("xl/workbook.xml") is null)
            {
                warnings.Add($"{fileName} is an Office package but not a workbook; only its VBA was read.");
                return empty;
            }
            return new Reader(zip, fileName, vba.Project, warnings).Run();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or ArgumentException)
        {
            warnings.Add($"{fileName}: the workbook parts could not be read ({ex.Message}).");
            return empty;
        }
    }

    private sealed class Reader
    {
        private readonly ZipArchive _zip;
        private readonly string _file;
        private readonly VbaProject? _vba;
        private readonly List<string> _warnings;
        private readonly List<string> _shared = new();
        private readonly Dictionary<string, Dictionary<string, string>> _cellText = new();   // sheet → cell → text
        private readonly Dictionary<string, List<string>> _routines;                          // module → routine names
        private readonly HashSet<string> _tableNames = new(StringComparer.OrdinalIgnoreCase);

        public Reader(ZipArchive zip, string file, VbaProject? vba, List<string> warnings)
        {
            _zip = zip; _file = file; _vba = vba; _warnings = warnings;
            _routines = (vba?.Modules ?? Array.Empty<VbaModule>())
                .ToDictionary(m => m.Name, m => RoutineNames(m.Source), StringComparer.OrdinalIgnoreCase);
        }

        public WorkbookSurfaceResult Run()
        {
            var wb = Load("xl/workbook.xml")!;
            var wbRels = Rels("xl/workbook.xml");
            var sst = Load("xl/sharedStrings.xml");
            if (sst is not null)
                foreach (var si in sst.Root!.Elements(S + "si")) _shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));

            var sheetEls = wb.Root!.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? new();
            var sheetNames = sheetEls.Select(e => (string?)e.Attribute("name") ?? "").ToList();

            // First pass: cells, so labels and name lookups can see every sheet.
            var parsed = new List<(XElement El, string Part, XDocument? Doc)>();
            foreach (var el in sheetEls)
            {
                var rid = (string?)el.Attribute(R + "id");
                var part = rid is not null && wbRels.TryGetValue(rid, out var t) ? t.Target : null;
                var doc = part is null ? null : Load(part);
                parsed.Add((el, part ?? "", doc));
                if (doc is not null) _cellText[(string?)el.Attribute("name") ?? ""] = CellTexts(doc);
            }

            var names = ReadNames(wb, sheetNames);
            var nameSet = new HashSet<string>(names.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            // Tables are referred to by name in formulas (mortality[age]); collect them before the formulas are read.
            foreach (var (_, part, doc) in parsed)
                if (doc is not null)
                    foreach (var t in ReadTables(doc.Root!, part, Rels(part))) _tableNames.Add(t.Name);

            var sheets = new List<WorkbookSheet>();
            foreach (var (el, part, doc) in parsed)
            {
                var name = (string?)el.Attribute("name") ?? "";
                var state = (string?)el.Attribute("state") ?? "visible";
                if (doc is null)
                {
                    // Chart sheets and dialog sheets have no cells.
                    sheets.Add(new WorkbookSheet(name, null, state, null, 0, 0, Array.Empty<WorkbookFormula>(), Array.Empty<WorkbookValidation>(),
                        Array.Empty<WorkbookControl>(), Array.Empty<WorkbookTable>(), Array.Empty<string>()));
                    continue;
                }
                sheets.Add(ReadSheet(name, state, part, doc, sheetNames, nameSet));
            }

            var events = ReadEvents(sheets);
            var reach = ReadCodeReach(sheets, names);
            var externals = ReadExternals(wbRels);
            return new WorkbookSurfaceResult(_file, sheets, names, events, reach, externals, _vba, _warnings);
        }

        // ── Names ──────────────────────────────────────────────────────────

        private List<WorkbookName> ReadNames(XDocument wb, List<string> sheetNames)
        {
            var list = new List<WorkbookName>();
            foreach (var d in wb.Root!.Element(S + "definedNames")?.Elements(S + "definedName") ?? Enumerable.Empty<XElement>())
            {
                var name = (string?)d.Attribute("name") ?? "";
                var local = (int?)d.Attribute("localSheetId");
                var scope = local is int i && i >= 0 && i < sheetNames.Count ? sheetNames[i] : null;
                var hidden = (bool?)d.Attribute("hidden") ?? false;
                // Excel's own bookkeeping (_xlnm.Print_Area, _FilterDatabase) is not the author's vocabulary.
                if (name.StartsWith("_xlnm.", StringComparison.Ordinal) || name.StartsWith("_xl", StringComparison.Ordinal)) continue;
                list.Add(new WorkbookName(name, d.Value, scope, hidden, LabelForRef(d.Value, scope)));
            }
            return list;
        }

        // ── One sheet ─────────────────────────────────────────────────────

        private WorkbookSheet ReadSheet(string name, string state, string part, XDocument doc, List<string> sheetNames, HashSet<string> nameSet)
        {
            var root = doc.Root!;
            var codeName = (string?)root.Element(S + "sheetPr")?.Attribute("codeName");
            var used = (string?)root.Element(S + "dimension")?.Attribute("ref");
            var rels = Rels(part);

            int values = 0, formulaCells = 0;
            var formulas = new List<WorkbookFormula>();
            var reads = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in root.Element(S + "sheetData")?.Descendants(S + "c") ?? Enumerable.Empty<XElement>())
            {
                var f = c.Element(S + "f");
                if (f is null) { if (c.Element(S + "v") is not null || c.Element(S + "is") is not null) values++; continue; }
                formulaCells++;
                // A shared formula's followers carry only its index; the leader holds the text and the range.
                if (f.Value.Length == 0) continue;
                var refs = FormulaRefs(f.Value, name, sheetNames, nameSet, _tableNames);
                foreach (var r in refs.Where(r => r.StartsWith("sheet:", StringComparison.Ordinal))) reads.Add(r[6..]);
                if (formulas.Count < MaxFormulasPerSheet)
                    formulas.Add(new WorkbookFormula((string?)c.Attribute("r") ?? "", f.Value, (string?)f.Attribute("ref"),
                        (string?)f.Attribute("t") == "array", refs));
            }
            if (formulas.Count >= MaxFormulasPerSheet)
                _warnings.Add($"{name}: only the first {MaxFormulasPerSheet:N0} formulas are listed; all {formulaCells:N0} are counted.");

            return new WorkbookSheet(name, codeName, state, used, values, formulaCells, formulas,
                ReadValidations(root, name), ReadControls(root, part, rels, name, codeName), ReadTables(root, part, rels),
                reads.ToList());
        }

        private List<WorkbookValidation> ReadValidations(XElement root, string sheet)
        {
            var list = new List<WorkbookValidation>();
            // Plain ones in <dataValidations>; ones whose list sits on another sheet in the x14 extension.
            foreach (var dv in root.Descendants().Where(e => e.Name.LocalName == "dataValidation"))
            {
                var sqref = (string?)dv.Attribute("sqref") ?? dv.Elements().FirstOrDefault(e => e.Name.LocalName == "sqref")?.Value ?? "";
                var type = (string?)dv.Attribute("type") ?? "any";
                var f1 = dv.Elements().FirstOrDefault(e => e.Name.LocalName == "formula1");
                var source = f1 is null ? null : (f1.Elements().FirstOrDefault()?.Value ?? f1.Value);
                if (source is not null && source.Length >= 2 && source[0] == '"' && source[^1] == '"') source = source[1..^1];
                if (type is not ("list" or "custom" or "any") && source is not null)
                {
                    // Numbers, dates and lengths carry a comparison: "between 18 and 80", "greaterThan 0".
                    var f2 = dv.Elements().FirstOrDefault(e => e.Name.LocalName == "formula2");
                    var upper = f2 is null ? null : (f2.Elements().FirstOrDefault()?.Value ?? f2.Value);
                    var op = (string?)dv.Attribute("operator") ?? "between";
                    source = op switch
                    {
                        "between" => upper is null ? $">= {source}" : $"{source} to {upper}",
                        "notBetween" => upper is null ? $"< {source}" : $"not {source} to {upper}",
                        "equal" => $"= {source}", "notEqual" => $"<> {source}",
                        "greaterThan" => $"> {source}", "greaterThanOrEqual" => $">= {source}",
                        "lessThan" => $"< {source}", "lessThanOrEqual" => $"<= {source}",
                        _ => source,
                    };
                }
                var first = sqref.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var firstCell = first.Split(':')[0];
                var prompt = (string?)dv.Attribute("promptTitle") ?? (string?)dv.Attribute("prompt");
                list.Add(new WorkbookValidation(sqref, type, source, LabelBeside(sheet, firstCell), prompt));
            }
            return list;
        }

        private List<WorkbookTable> ReadTables(XElement root, string part, Dictionary<string, Rel> rels)
        {
            var list = new List<WorkbookTable>();
            foreach (var tp in root.Element(S + "tableParts")?.Elements(S + "tablePart") ?? Enumerable.Empty<XElement>())
            {
                var rid = (string?)tp.Attribute(R + "id");
                if (rid is null || !rels.TryGetValue(rid, out var rel)) continue;
                var t = Load(rel.Target)?.Root;
                if (t is null) continue;
                var cols = t.Element(S + "tableColumns")?.Elements(S + "tableColumn").Select(c => (string?)c.Attribute("name") ?? "").ToList() ?? new();
                list.Add(new WorkbookTable((string?)t.Attribute("displayName") ?? (string?)t.Attribute("name") ?? "", (string?)t.Attribute("ref") ?? "", cols));
            }
            return list;
        }

        // ── Controls ──────────────────────────────────────────────────────

        private List<WorkbookControl> ReadControls(XElement root, string part, Dictionary<string, Rel> rels, string sheet, string? codeName)
        {
            var list = new List<WorkbookControl>();
            var sheetRoutines = codeName is not null && _routines.TryGetValue(codeName, out var rs) ? rs : new List<string>();

            // <controls> names every control and points at its properties (form control) or its ActiveX part.
            var named = new Dictionary<string, (string Name, Rel Rel)>();          // shapeId → name, part
            foreach (var c in root.Descendants(S + "control"))
            {
                var id = (string?)c.Attribute("shapeId");
                var rid = (string?)c.Attribute(R + "id");
                if (id is null || rid is null || named.ContainsKey(id) || !rels.TryGetValue(rid, out var rel)) continue;
                named[id] = ((string?)c.Attribute("name") ?? id, rel);
            }

            // Form controls: the legacy VML drawing carries kind, caption, macro and linked cell.
            var vmlRid = (string?)root.Element(S + "legacyDrawing")?.Attribute(R + "id");
            var seen = new HashSet<string>();
            if (vmlRid is not null && rels.TryGetValue(vmlRid, out var vmlRel))
            {
                foreach (var shape in VmlShapes(LoadText(vmlRel.Target)))
                {
                    if (shape.ObjectType is "Note" or "Pict" or null) continue;      // comments, and ActiveX placeholders
                    // Without a <controls> entry the VML id is often just the internal spid; say what it is instead.
                    var display = named.TryGetValue(shape.ShapeId ?? "", out var n) ? n.Name
                        : shape.Id is { } vid && !vid.StartsWith("_x0000_", StringComparison.Ordinal) ? vid
                        : $"{KindOfFormControl(shape.ObjectType)} {shape.ShapeId}";
                    seen.Add(shape.ShapeId ?? display);
                    var (module, routine, found) = ResolveMacro(shape.Macro);
                    list.Add(new WorkbookControl(display, "form", KindOfFormControl(shape.ObjectType), shape.Text,
                        shape.Macro, module, routine, found, shape.Link, shape.Range, Array.Empty<string>()));
                }
            }

            // ActiveX controls: the kind is in the class id; code reaches them through handlers named after them.
            foreach (var (id, (name, rel)) in named)
            {
                if (seen.Contains(id) || !rel.Type.EndsWith("/control", StringComparison.Ordinal)) continue;
                var ax = Load(rel.Target)?.Root;
                if (ax is null || ax.Name.LocalName != "ocx") continue;
                var classId = ax.Attributes().FirstOrDefault(a => a.Name.LocalName == "classid")?.Value ?? "";
                var kind = ActiveXKinds.TryGetValue(classId, out var k) ? k : $"ActiveX {classId}";
                var handlers = sheetRoutines.Where(r => r.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)).ToList();
                list.Add(new WorkbookControl(name, "activex", kind, null, null, handlers.Count > 0 ? codeName : null,
                    null, handlers.Count > 0, null, null, handlers));
            }

            // Shapes and pictures with a macro assigned (a common "button" in templates).
            var drawRid = (string?)root.Element(S + "drawing")?.Attribute(R + "id");
            if (drawRid is not null && rels.TryGetValue(drawRid, out var drawRel) && Load(drawRel.Target)?.Root is { } dr)
            {
                foreach (var el in dr.Descendants().Where(e => e.Attribute("macro") is { Value.Length: > 0 }))
                {
                    var macro = el.Attribute("macro")!.Value;
                    var nv = el.Descendants(Xdr + "cNvPr").FirstOrDefault();
                    var text = string.Concat(el.Descendants(A + "t").Select(t => t.Value)).Trim();
                    var (module, routine, found) = ResolveMacro(macro);
                    list.Add(new WorkbookControl((string?)nv?.Attribute("name") ?? "shape", "shape", el.Name.LocalName == "pic" ? "Picture" : "Shape",
                        text.Length > 0 ? text : null, macro, module, routine, found, null, null, Array.Empty<string>()));
                }
            }
            // Two controls can share a name (copied buttons keep it); a control is cited by its name, so keep them apart.
            foreach (var g in list.Select((c, i) => (c, i)).GroupBy(x => x.c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var n = 1;
                foreach (var (c, i) in g.Skip(1)) list[i] = c with { Name = $"{c.Name} ({++n})" };
            }
            return list;
        }

        private static string KindOfFormControl(string? t) => t switch
        {
            "Checkbox" => "CheckBox",
            "Drop" => "DropDown",
            "Radio" => "OptionButton",
            "Spin" => "Spinner",
            "Scroll" => "ScrollBar",
            "GBox" => "GroupBox",
            "Edit" => "EditBox",
            _ => t ?? "Control",
        };

        private sealed record VmlShape(string? Id, string? ShapeId, string? ObjectType, string? Text, string? Macro, string? Link, string? Range);

        private static readonly Regex VmlShapeRe = new(@"<v:shape\b(?<attrs>[^>]*)>(?<body>.*?)</v:shape>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex AttrRe = new(@"(?<k>[\w:]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')");
        private static readonly Regex TagRe = new(@"<[^>]+>");

        /// <summary>VML from Excel is not always well-formed XML (bare <c>&lt;br&gt;</c>), so it is read by pattern.</summary>
        private static IEnumerable<VmlShape> VmlShapes(string? vml)
        {
            if (string.IsNullOrEmpty(vml)) yield break;
            foreach (Match m in VmlShapeRe.Matches(vml))
            {
                var attrs = AttrRe.Matches(m.Groups["attrs"].Value).ToDictionary(a => a.Groups["k"].Value, a => a.Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
                var body = m.Groups["body"].Value;
                // Excel writes the shape id as o:spid, or (older files) only as the id itself.
                var spid = attrs.GetValueOrDefault("o:spid") ?? attrs.GetValueOrDefault("id");
                var shapeId = spid is not null && spid.StartsWith("_x0000_s", StringComparison.Ordinal) ? spid[8..] : null;
                var objectType = Regex.Match(body, @"<x:ClientData\s+ObjectType=""(\w+)""").Groups[1].Value;
                string? Tag(string t) { var v = Regex.Match(body, $@"<x:{t}>(.*?)</x:{t}>", RegexOptions.Singleline).Groups[1].Value.Trim(); return v.Length > 0 ? System.Net.WebUtility.HtmlDecode(v) : null; }
                var tb = Regex.Match(body, @"<v:textbox\b[^>]*>(.*?)</v:textbox>", RegexOptions.Singleline).Groups[1].Value;
                var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(TagRe.Replace(tb.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase), ""), @"\s+", " ")).Trim();
                yield return new VmlShape(attrs.GetValueOrDefault("id"), shapeId, objectType.Length > 0 ? objectType : null,
                    text.Length > 0 ? text : null, Tag("FmlaMacro"), Tag("FmlaLink"), Tag("FmlaRange"));
            }
        }

        /// <summary><c>[0]!Module.Routine</c>, <c>'Book.xlsm'!Routine</c> or a bare routine → where it is in this project.</summary>
        private (string? Module, string? Routine, bool Found) ResolveMacro(string? macro)
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
                return (mod, routine, _routines.TryGetValue(mod, out var rs) && rs.Contains(routine, StringComparer.OrdinalIgnoreCase));
            }
            // Unqualified: Excel looks through the standard modules.
            var standard = _vba?.Modules.Where(x => x.Kind == VbaModuleKind.Standard).Select(x => x.Name) ?? Enumerable.Empty<string>();
            foreach (var mod in standard.Concat(_routines.Keys))
                if (_routines[mod].Contains(m, StringComparer.OrdinalIgnoreCase)) return (mod, m, true);
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

        private List<WorkbookEvent> ReadEvents(List<WorkbookSheet> sheets)
        {
            var list = new List<WorkbookEvent>();
            if (_vba is null) return list;
            var byCode = sheets.Where(s => s.CodeName is not null).ToDictionary(s => s.CodeName!, s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var m in _vba.Modules.Where(m => m.Kind == VbaModuleKind.Document))
                foreach (var r in _routines[m.Name])
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
                foreach (var r in _routines[m.Name].Where(r => r.Equals("Auto_Open", StringComparison.OrdinalIgnoreCase) || r.Equals("Auto_Close", StringComparison.OrdinalIgnoreCase)))
                    list.Add(new WorkbookEvent(m.Name, r, r, null));
            return list;
        }

        private static readonly Regex StringCallRe = new(@"\b(?<fn>Sheets|Worksheets|Range|Names)\s*\(\s*""(?<arg>[^""]+)""", RegexOptions.IgnoreCase);
        private static readonly Regex BracketRe = new(@"(?<![\w\]""])\[(?<arg>[A-Za-z_][\w.]*|\$?[A-Z]{1,3}\$?\d+(?::\$?[A-Z]{1,3}\$?\d+)?)\]");
        private static readonly Regex CellAddrRe = new(@"^\$?[A-Z]{1,3}\$?\d+(?::\$?[A-Z]{1,3}\$?\d+)?$", RegexOptions.IgnoreCase);
        private static readonly Regex IdentRe = new(@"\b[A-Za-z_]\w*\b");

        private List<WorkbookCodeReach> ReadCodeReach(List<WorkbookSheet> sheets, List<WorkbookName> names)
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

        // ── Links out of the workbook ──────────────────────────────────────

        private static readonly Regex SecretRe = new(@"(?i)\b(password|pwd|user\s*id|uid|apikey|api_key|key|token|secret)\s*=\s*[^;""]*");

        private List<WorkbookExternal> ReadExternals(Dictionary<string, Rel> wbRels)
        {
            var list = new List<WorkbookExternal>();
            foreach (var rel in wbRels.Values.Where(r => r.Type.EndsWith("/externalLink", StringComparison.Ordinal)))
            {
                var lr = Rels(rel.Target);
                var target = lr.Values.FirstOrDefault(r => r.External)?.RawTarget;
                var shown = target is null ? null : Redact(Uri.UnescapeDataString(target));
                // Named by the workbook it points at; the package part (externalLink1.xml) means nothing to a reader.
                var linkName = shown is null ? Path.GetFileName(rel.Target) : Path.GetFileName(shown.Replace('\\', '/').TrimEnd('/'));
                list.Add(new WorkbookExternal("workbook", string.IsNullOrEmpty(linkName) ? Path.GetFileName(rel.Target) : linkName, shown));
            }
            if (Load("xl/connections.xml")?.Root is { } conns)
                foreach (var c in conns.Elements(S + "connection"))
                {
                    var type = (int?)c.Attribute("type") switch { 1 => "odbc", 2 => "dao", 4 => "web", 5 => "oledb", 6 => "text", 7 => "ado", 8 => "dsp", _ => "connection" };
                    var target = (string?)c.Element(S + "dbPr")?.Attribute("connection")
                                 ?? (string?)c.Element(S + "webPr")?.Attribute("url")
                                 ?? (string?)c.Element(S + "textPr")?.Attribute("sourceFile");
                    list.Add(new WorkbookExternal(type, (string?)c.Attribute("name") ?? "", target is null ? null : Redact(target)));
                }
            return list;
        }

        /// <summary>Connection strings can carry credentials; those never leave the reader.</summary>
        public static string Redact(string s) => SecretRe.Replace(s, m => m.Groups[1].Value + "=***");

        // ── Formulas ──────────────────────────────────────────────────────

        private static readonly Regex QuotedSheetRefRe = new(@"'((?:[^']|'')+)'!");
        private static readonly Regex PlainSheetRefRe = new(@"(?<![\w.'\]])([A-Za-z_][\w.]*)!");
        private static readonly Regex ExternalRefRe = new(@"\[(\d+)\]");
        private static readonly Regex TableRefRe = new(@"\b([A-Za-z_][\w.]*)\[");

        private static List<string> FormulaRefs(string formula, string self, List<string> sheetNames, HashSet<string> nameSet, HashSet<string> tables)
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

        private static List<string> RoutineNames(string source) =>
            RoutineRe.Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // ── Cells and labels ──────────────────────────────────────────────

        private Dictionary<string, string> CellTexts(XDocument doc)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in doc.Root!.Element(S + "sheetData")?.Descendants(S + "c") ?? Enumerable.Empty<XElement>())
            {
                var t = (string?)c.Attribute("t");
                string? text = t switch
                {
                    "s" when int.TryParse(c.Element(S + "v")?.Value, out var i) && i >= 0 && i < _shared.Count => _shared[i],
                    "inlineStr" => string.Concat(c.Descendants(S + "t").Select(x => x.Value)),
                    "str" => null,                                       // a formula's text result, not a label
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text) && c.Attribute("r") is { } r) map[r.Value] = text.Trim();
            }
            return map;
        }

        private static readonly Regex SingleCellRefRe = new(@"^(?:'?(?<sheet>(?:[^'!]|'')+)'?!)?\$?(?<col>[A-Z]{1,3})\$?(?<row>\d+)$", RegexOptions.IgnoreCase);

        private string? LabelForRef(string refersTo, string? scope)
        {
            var m = SingleCellRefRe.Match(refersTo.Trim());
            if (!m.Success) return null;
            var sheet = m.Groups["sheet"].Success ? m.Groups["sheet"].Value.Replace("''", "'") : scope;
            return sheet is null ? null : LabelBeside(sheet, m.Groups["col"].Value + m.Groups["row"].Value);
        }

        /// <summary>The nearest text to the left on the same row (within three cells), else the cell above.</summary>
        private string? LabelBeside(string sheet, string cell)
        {
            if (!_cellText.TryGetValue(sheet, out var texts)) return null;
            var m = Regex.Match(cell.Replace("$", ""), @"^([A-Z]{1,3})(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var col = ColumnNumber(m.Groups[1].Value);
            var row = int.Parse(m.Groups[2].Value);
            for (var c = col - 1; c >= Math.Max(1, col - 3); c--)
                if (texts.TryGetValue(ColumnName(c) + row, out var t)) return Trim(t);
            return row > 1 && texts.TryGetValue(ColumnName(col) + (row - 1), out var above) ? Trim(above) : null;

            static string Trim(string t) => t.Length > 80 ? t[..80] + "…" : t;
        }

        private static int ColumnNumber(string letters) => letters.ToUpperInvariant().Aggregate(0, (n, ch) => n * 26 + (ch - 'A' + 1));
        private static string ColumnName(int n) { var s = ""; while (n > 0) { var r = (n - 1) % 26; s = (char)('A' + r) + s; n = (n - 1) / 26; } return s; }

        // ── Package plumbing ──────────────────────────────────────────────

        private sealed record Rel(string Type, string Target, string RawTarget, bool External);

        private Dictionary<string, Rel> Rels(string part)
        {
            var dir = Path.GetDirectoryName(part)?.Replace('\\', '/') ?? "";
            var relsPath = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + Path.GetFileName(part) + ".rels";
            var map = new Dictionary<string, Rel>();
            var doc = Load(relsPath);
            if (doc is null) return map;
            foreach (var r in doc.Root!.Elements(Pkg + "Relationship"))
            {
                var id = (string?)r.Attribute("Id"); var raw = (string?)r.Attribute("Target") ?? "";
                if (id is null) continue;
                var external = (string?)r.Attribute("TargetMode") == "External";
                map[id] = new Rel((string?)r.Attribute("Type") ?? "", external ? raw : Resolve(dir, raw), raw, external);
            }
            return map;
        }

        private static string Resolve(string dir, string target)
        {
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = (dir.Length > 0 ? dir + "/" + target : target).Split('/').ToList();
            var stack = new List<string>();
            foreach (var p in parts)
            {
                if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
                else if (p != "." && p.Length > 0) stack.Add(p);
            }
            return string.Join('/', stack);
        }

        private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        private XDocument? Load(string part)
        {
            var e = _zip.GetEntry(part);
            if (e is null) return null;
            if (e.Length > MaxPartBytes) { _warnings.Add($"{part} claims {e.Length:N0} bytes, over the limit; not read."); return null; }
            using var s = e.Open();
            using var r = XmlReader.Create(s, Safe);
            return XDocument.Load(r);
        }

        private string? LoadText(string part)
        {
            var e = _zip.GetEntry(part);
            if (e is null || e.Length > MaxPartBytes) return null;
            using var sr = new StreamReader(e.Open(), Encoding.UTF8);
            return sr.ReadToEnd();
        }
    }
}
