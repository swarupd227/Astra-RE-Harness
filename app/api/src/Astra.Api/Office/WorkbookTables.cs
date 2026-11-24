using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Office;

/// <summary>One sheet that is a table of values (rates, factors, tariffs): where it starts and every row, as stored.</summary>
public sealed record DataTable(string Sheet, string State, int Rows, int Columns, int NumericCells, object?[][] Grid)
{
    /// <summary>File name inside a package: lower-case, letters and digits, e.g. "male_nonsmoker".</summary>
    public string Slug
    {
        get
        {
            var s = new StringBuilder();
            foreach (var ch in Sheet.ToLowerInvariant()) s.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            var t = s.ToString().Trim('_');
            while (t.Contains("__")) t = t.Replace("__", "_");
            return t.Length == 0 ? "sheet" : t;
        }
    }
}

/// <summary>
/// Reads the lookup tables out of a workbook's sheets (<c>.xlsx</c>/<c>.xlsm</c>) so a generated package can ship the
/// real values instead of a model's guess at them. A sheet counts as a table when it is mostly numeric constants (not
/// formulas) and big enough to be a lookup; the sheet a form sits on (few constants, many formulas) does not.
/// Cell positions are kept: row i of the grid is sheet row i+1, column j is column j+1 (so an <c>Offset(row, col)</c>
/// from A4 in a macro maps straight onto the grid).
/// </summary>
public static class WorkbookTables
{
    private const long MaxPartBytes = 24_000_000;
    private const int MinNumericCells = 24;
    private const int MaxTables = 12;
    private const int MaxCellsTotal = 120_000;
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    public static IReadOnlyList<DataTable> Read(byte[] file)
    {
        var result = new List<DataTable>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
            var wb = Load(zip, "xl/workbook.xml");
            if (wb is null) return result;
            var rels = Load(zip, "xl/_rels/workbook.xml.rels");
            var targets = rels?.Root?.Elements().ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => (string?)e.Attribute("Target") ?? "") ?? new();
            var sst = SharedStrings(zip);
            var total = 0;
            foreach (var s in wb.Root?.Element(M + "sheets")?.Elements(M + "sheet") ?? Enumerable.Empty<XElement>())
            {
                var name = (string?)s.Attribute("name") ?? "";
                var state = (string?)s.Attribute("state") ?? "visible";
                var rid = (string?)s.Attribute(R + "id") ?? "";
                if (!targets.TryGetValue(rid, out var target)) continue;
                var table = ReadSheet(zip, Resolve(target), name, state, sst);
                if (table is null) continue;
                total += table.Rows * table.Columns;
                if (result.Count >= MaxTables || total > MaxCellsTotal) break;
                result.Add(table);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException) { /* not an OOXML book: no tables */ }
        return result;
    }

    private static DataTable? ReadSheet(ZipArchive zip, string path, string name, string state, List<string> sst)
    {
        var doc = Load(zip, path);
        if (doc is null) return null;
        var cells = new Dictionary<(int Row, int Col), object?>();
        int numeric = 0, formulas = 0, maxRow = 0, maxCol = 0;
        foreach (var c in doc.Descendants(M + "c"))
        {
            var r = (string?)c.Attribute("r");
            if (r is null || !TryRef(r, out var row, out var col)) continue;
            var hasFormula = c.Element(M + "f") is not null;
            var v = c.Element(M + "v")?.Value;
            var t = (string?)c.Attribute("t");
            object? value = null;
            if (v is not null)
            {
                value = t switch
                {
                    "s" when int.TryParse(v, out var i) && i >= 0 && i < sst.Count => sst[i],
                    "s" => null,
                    "str" or "e" => v,
                    "b" => v == "1",
                    _ => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (object)d : v,
                };
            }
            else if (t == "inlineStr")
                value = c.Element(M + "is")?.Value;
            if (value is null) continue;
            if (hasFormula) formulas++;
            else if (value is double) numeric++;
            cells[(row, col)] = value;
            maxRow = Math.Max(maxRow, row);
            maxCol = Math.Max(maxCol, col);
        }
        // A lookup table: plenty of numeric constants, and constants outnumber formulas.
        if (numeric < MinNumericCells || formulas > numeric / 4 || maxRow == 0 || maxCol == 0) return null;

        var grid = new object?[maxRow][];
        for (var r = 0; r < maxRow; r++)
        {
            grid[r] = new object?[maxCol];
            for (var c = 0; c < maxCol; c++)
                if (cells.TryGetValue((r + 1, c + 1), out var v)) grid[r][c] = v;
        }
        return new DataTable(name, state, maxRow, maxCol, numeric, grid);
    }

    /// <summary>The package file for a table: <c>{"sheet", "rows", "columns", "grid"}</c>, rows as arrays so order is kept.</summary>
    public static string ToJson(DataTable t)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("sheet", t.Sheet);
            w.WriteString("note", "Values copied from the workbook's sheet. grid[i][j] is the cell in sheet row i+1, column j+1; null is an empty cell.");
            w.WriteNumber("rows", t.Rows);
            w.WriteNumber("columns", t.Columns);
            w.WritePropertyName("grid");
            w.WriteStartArray();
            foreach (var row in t.Grid)
            {
                w.WriteStartArray();
                foreach (var v in row)
                {
                    switch (v)
                    {
                        case null: w.WriteNullValue(); break;
                        case double d: w.WriteNumberValue(d); break;
                        case bool b: w.WriteBooleanValue(b); break;
                        default: w.WriteStringValue(v.ToString()); break;
                    }
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>What the code generator is told about the tables: where each file is, its shape and a few real rows.</summary>
    public static string DescribeForPrompt(IReadOnlyList<DataTable> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The workbook keeps its lookup values on these sheets. The platform copies them, unchanged, into the package");
        sb.AppendLine("as the JSON files below (they are added to your output; do NOT write them yourself, and do NOT type or");
        sb.AppendLine("invent any value from them). Load them at run time. `grid[i][j]` is the cell in sheet row i+1, column j+1,");
        sb.AppendLine("so a macro's `Offset(r, c)` from cell A4 is `grid[3 + r][c]`.");
        sb.AppendLine();
        foreach (var t in tables)
        {
            sb.AppendLine($"- `backend/data/{t.Slug}.json` — sheet \"{t.Sheet}\"{(t.State == "visible" ? "" : $" ({t.State})")}, {t.Rows} rows × {t.Columns} columns, {t.NumericCells} numeric cells. First rows (real values, row number first):");
            var shown = 0;
            for (var i = 0; i < t.Grid.Length && shown < 5; i++)
            {
                if (t.Grid[i].All(v => v is null)) continue;
                sb.AppendLine($"    row {i + 1}: " + string.Join(", ", t.Grid[i].Take(8).Select(Show)) + (t.Columns > 8 ? ", …" : ""));
                shown++;
            }
        }
        return sb.ToString();
    }

    private static string Show(object? v) => v switch
    {
        null => "·",
        double d => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        _ => "\"" + v + "\"",
    };

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var e = zip.GetEntry(path);
        if (e is null || e.Length > MaxPartBytes) return null;
        using var s = e.Open();
        using var r = XmlReader.Create(s, Safe);
        return XDocument.Load(r);
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var doc = Load(zip, "xl/sharedStrings.xml");
        if (doc is null) return list;
        foreach (var si in doc.Root?.Elements(M + "si") ?? Enumerable.Empty<XElement>())
            list.Add(string.Concat(si.Descendants(M + "t").Select(t => t.Value)));
        return list;
    }

    private static string Resolve(string target) =>
        target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.Replace("../", "");

    private static bool TryRef(string r, out int row, out int col)
    {
        row = 0; col = 0;
        var i = 0;
        while (i < r.Length && char.IsLetter(r[i])) { col = col * 26 + (char.ToUpperInvariant(r[i]) - 'A' + 1); i++; }
        return i > 0 && i < r.Length && int.TryParse(r[i..], out row) && row > 0;
    }
}
