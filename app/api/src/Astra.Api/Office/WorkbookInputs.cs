using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Office;

/// <summary>
/// What a generated form needs from the workbook it replaces: the current values on the sheets a module reads (its
/// inputs as the user left them, the Yes/No selection columns, the premium it shows), and each input rule's allowed
/// values with a list reference such as <c>=Lists!$A$2:$A$9</c> resolved to the values themselves. Without it a package
/// invented its drop-down choices and started from empty selections, and the real service refused what it sent.
/// Pure: reads only the file it is given.
/// </summary>
public static class WorkbookInputs
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const int MaxCellsPerSheet = 400;
    public const int MaxChars = 30_000;

    public sealed record Cell(string Address, object Value, bool Formula);

    /// <summary>Every non-empty cell of the named sheets, in sheet order then row then column; formulas keep their cached result.</summary>
    public static Dictionary<string, List<Cell>> ReadSheets(byte[] file, IEnumerable<string> sheetNames)
    {
        var wanted = new HashSet<string>(sheetNames, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<Cell>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
            var wb = WorkbookTables.Load(zip, "xl/workbook.xml");
            if (wb is null) return result;
            var rels = WorkbookTables.Load(zip, "xl/_rels/workbook.xml.rels");
            var targets = rels?.Root?.Elements().ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => (string?)e.Attribute("Target") ?? "") ?? new();
            var sst = WorkbookTables.SharedStrings(zip);
            foreach (var s in wb.Root?.Element(M + "sheets")?.Elements(M + "sheet") ?? Enumerable.Empty<XElement>())
            {
                var name = (string?)s.Attribute("name") ?? "";
                if (!wanted.Contains(name) || !targets.TryGetValue((string?)s.Attribute(R + "id") ?? "", out var target)) continue;
                var doc = WorkbookTables.Load(zip, WorkbookTables.Resolve(target));
                if (doc is null) continue;
                var cells = new List<(int Row, int Col, Cell C)>();
                foreach (var c in doc.Descendants(M + "c"))
                {
                    var r = (string?)c.Attribute("r");
                    if (r is null || !WorkbookTables.TryRef(r, out var row, out var col)) continue;
                    var v = c.Element(M + "v")?.Value;
                    var t = (string?)c.Attribute("t");
                    object? value = t switch
                    {
                        "s" when int.TryParse(v, out var i) && i >= 0 && i < sst.Count => sst[i],
                        "inlineStr" => c.Element(M + "is")?.Value,
                        "b" when v is not null => v == "1",
                        _ when v is null => null,
                        "str" or "e" => v,
                        _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : v,
                    };
                    if (value is null || value is string { Length: 0 }) continue;
                    cells.Add((row, col, new Cell(r, value, c.Element(M + "f") is not null)));
                }
                result[name] = cells.OrderBy(x => x.Row).ThenBy(x => x.Col).Select(x => x.C).ToList();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException) { /* not an OOXML book */ }
        return result;
    }

    /// <summary>
    /// The allowed values of an input rule: an inline list (<c>"Yes,No"</c>) split, a range (<c>=Lists!$A$2:$A$9</c>,
    /// or a bare <c>$A$2:$A$9</c> on <paramref name="ruleSheet"/>) looked up in <paramref name="sheets"/>. Null when the
    /// rule is not a list or its range is not among the sheets read.
    /// </summary>
    public static IReadOnlyList<string>? AllowedValues(WorkbookValidation v, string ruleSheet, IReadOnlyDictionary<string, List<Cell>> sheets)
    {
        if (!string.Equals(v.Type, "list", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(v.Source)) return null;
        var src = v.Source.Trim();
        if (!IsReference(src))
            return src.Trim('"').Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        src = src.TrimStart('=');
        var bang = src.LastIndexOf('!');
        var sheet = bang > 0 ? src[..bang].Trim('\'') : ruleSheet;
        var range = (bang > 0 ? src[(bang + 1)..] : src).Replace("$", "");
        if (!sheets.TryGetValue(sheet, out var cells)) return null;
        var ends = range.Split(':');
        if (!WorkbookTables.TryRef(ends[0], out var r1, out var c1)) return null;
        var (r2, c2) = (r1, c1);
        if (ends.Length > 1 && !WorkbookTables.TryRef(ends[1], out r2, out c2)) return null;
        return cells.Where(c => WorkbookTables.TryRef(c.Address, out var r, out var col)
                                && r >= Math.Min(r1, r2) && r <= Math.Max(r1, r2) && col >= Math.Min(c1, c2) && col <= Math.Max(c1, c2))
            .Select(c => Show(c.Value).Trim('"')).ToList();
    }

    /// <summary>
    /// The sheets a module works on: those the surface saw it name, else the sheets whose name appears quoted in its code
    /// (VBA often keeps sheet names in constants: <c>Const SHEET_DASH = "1. Dashboard"</c>), else every visible sheet.
    /// </summary>
    public static IReadOnlyList<string> SheetsOf(WorkbookSurfaceResult surface, string module)
    {
        var reached = surface.CodeReach.FirstOrDefault(c => c.Module.Equals(module, StringComparison.OrdinalIgnoreCase))?.Sheets;
        if (reached is { Count: > 0 }) return reached;
        var code = surface.Vba?.Modules.FirstOrDefault(m => m.Name.Equals(module, StringComparison.OrdinalIgnoreCase))?.Source ?? "";
        var quoted = surface.Sheets.Where(s => code.Contains("\"" + s.Name + "\"", StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).ToList();
        if (quoted.Count > 0) return quoted;
        return surface.Sheets.Where(s => s.State == "visible").Select(s => s.Name).ToList();
    }

    /// <summary>
    /// The note for package generation: per sheet the module reads, its cells as <c>A1 = value</c> (formulas marked), then
    /// every input rule on those sheets with its allowed values. Null when none of the sheets could be read.
    /// </summary>
    public static string? Describe(byte[] file, WorkbookSurfaceResult surface, IReadOnlyList<string> moduleSheets)
    {
        if (moduleSheets.Count == 0) return null;
        // Lists a rule points at may live on another sheet (a hidden "Lists" sheet): read those too, to resolve them.
        var ruleSheets = surface.Sheets.Where(s => moduleSheets.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .SelectMany(s => s.Validations.Select(v => v.Source))
            .Where(src => src is not null && IsReference(src) && src.Contains('!'))
            .Select(src => src!.TrimStart('=')[..src!.TrimStart('=').LastIndexOf('!')].Trim('\''));
        var sheets = ReadSheets(file, moduleSheets.Concat(ruleSheets));
        if (!moduleSheets.Any(sheets.ContainsKey)) return null;

        var sb = new StringBuilder();
        sb.AppendLine("The workbook as the user left it — the sheets this routine's module reads. Start the form from these values (the");
        sb.AppendLine("current inputs, and the rows marked Yes in a selection column), offer exactly the allowed values listed below in");
        sb.AppendLine("each drop-down, and give every selection list (coverages, perils, options) a control. A value marked (formula) is");
        sb.AppendLine("what the workbook computed: use it to check a result, never as an input.");
        foreach (var name in moduleSheets.Where(sheets.ContainsKey))
        {
            var cells = sheets[name];
            sb.AppendLine().AppendLine($"### Sheet \"{name}\" — {cells.Count} filled cells{(cells.Count > MaxCellsPerSheet ? $", first {MaxCellsPerSheet} shown" : "")}");
            foreach (var c in cells.Take(MaxCellsPerSheet))
                sb.AppendLine($"- {c.Address} = {Show(c.Value)}{(c.Formula ? " (formula)" : "")}");
            if (sb.Length > MaxChars) break;
        }
        var rules = surface.Sheets.Where(s => moduleSheets.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .SelectMany(s => s.Validations.Select(v => (s.Name, v))).ToList();
        if (rules.Count > 0)
        {
            sb.AppendLine().AppendLine("### Input rules (sheet!cells \"label\": allowed values)");
            foreach (var (sheet, v) in rules.Take(60))
            {
                var allowed = AllowedValues(v, sheet, sheets);
                var what = allowed is not null ? "one of " + string.Join(" | ", allowed.Select(a => $"\"{a}\""))
                         : v.Source is null ? $"a {v.Type}" : $"a {v.Type} {v.Source}";
                sb.AppendLine($"- {sheet}!{Span(v.Cells)}{(v.Label is null ? "" : $" \"{v.Label}\"")}: {what}");
            }
        }
        var text = sb.ToString().TrimEnd();
        return text.Length > MaxChars ? text[..MaxChars] + "\n- … (cut to size)" : text;
    }

    /// <summary>A rule's source is a range (<c>=Lists!$A$2:$A$9</c>, <c>'Rates'!$V$3:$V$6</c>, <c>$A$1:$A$4</c>), not an inline list.</summary>
    private static bool IsReference(string src) =>
        src.StartsWith('=') || System.Text.RegularExpressions.Regex.IsMatch(src, @"^('[^']+'|[^,""]+)!\$?[A-Z]{1,3}\$?\d+(:\$?[A-Z]{1,3}\$?\d+)?$")
        || System.Text.RegularExpressions.Regex.IsMatch(src, @"^\$?[A-Z]{1,3}\$?\d+:\$?[A-Z]{1,3}\$?\d+$");

    /// <summary>"D5 D6 … D70" (how Excel lists a rule's cells) as "D5…D70 (66 cells)".</summary>
    private static string Span(string cells)
    {
        var parts = cells.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 4 ? cells : $"{parts[0]}…{parts[^1]} ({parts.Length} cells)";
    }

    private static string Show(object v) => v switch
    {
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => "\"" + v + "\"",
    };
}
