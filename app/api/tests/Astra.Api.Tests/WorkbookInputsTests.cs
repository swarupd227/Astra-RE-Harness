using System.IO.Compression;
using System.Text;
using Astra.Api.Office;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>A generated form starts where the workbook was and offers the workbook's own choices.</summary>
public class WorkbookInputsTests
{
    private static byte[] Book(params (string name, string cells)[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
            Put("xl/workbook.xml", $"<workbook {ns}><sheets>" +
                string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((s, i) => $"<Relationship Id=\"rId{i + 1}\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) + "</Relationships>");
            Put("xl/sharedStrings.xml", $"<sst {ns}><si><t>Yes</t></si></sst>");
            for (var i = 0; i < sheets.Length; i++)
                Put($"xl/worksheets/sheet{i + 1}.xml", $"<worksheet {ns}><sheetData>{sheets[i].cells}</sheetData></worksheet>");
        }
        return ms.ToArray();
    }

    private static string S(string r, string text) => $"<c r=\"{r}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static readonly byte[] File = Book(
        ("1. Dashboard",
            "<row r=\"5\">" + S("D5", "Trip cost") + "<c r=\"E5\"><v>6666</v></c></row>" +
            "<row r=\"9\">" + S("D9", "Exclusion") + S("E9", "None of Above Waived") + "</row>" +
            "<row r=\"15\"><c r=\"F15\"><f>SUM(E5:E9)</f><v>136.28</v></c></row>"),
        ("Coverage",
            "<row r=\"5\">" + S("B5", "Trip Cancellation") + "<c r=\"D5\" t=\"s\"><v>0</v></c></row>" +
            "<row r=\"6\">" + S("B6", "Baggage") + S("D6", "No") + "</row>"),
        ("Lists",
            "<row r=\"2\">" + S("A2", "None of Above Waived") + "</row><row r=\"3\">" + S("A3", "Pre-Ex Waived") + "</row>"),
        ("Unrelated", "<row r=\"1\">" + S("A1", "secret-ish") + "</row>"));

    [Fact]
    public void The_sheets_asked_for_are_read_with_values_shared_strings_and_formula_results()
    {
        var sheets = WorkbookInputs.ReadSheets(File, new[] { "1. Dashboard", "Coverage" });
        Assert.Equal(2, sheets.Count);
        var dash = sheets["1. Dashboard"];
        Assert.Contains(dash, c => c.Address == "E5" && c.Value is double d && d == 6666);
        Assert.Contains(dash, c => c.Address == "F15" && c.Formula && c.Value is double p && p == 136.28);
        Assert.Contains(sheets["Coverage"], c => c.Address == "D5" && (string)c.Value == "Yes");   // a shared string
        Assert.False(sheets.ContainsKey("Unrelated"));
    }

    [Fact]
    public void A_list_rule_gives_its_values_whether_inline_on_another_sheet_or_not_a_list()
    {
        var sheets = WorkbookInputs.ReadSheets(File, new[] { "1. Dashboard", "Lists" });
        var fromRange = WorkbookInputs.AllowedValues(new WorkbookValidation("E9", "list", "=Lists!$A$2:$A$3", "Exclusion", null), "1. Dashboard", sheets);
        Assert.Equal(new[] { "None of Above Waived", "Pre-Ex Waived" }, fromRange);
        var noEquals = WorkbookInputs.AllowedValues(new WorkbookValidation("E9", "list", "'Lists'!$A$2:$A$3", null, null), "1. Dashboard", sheets);
        Assert.Equal(new[] { "None of Above Waived", "Pre-Ex Waived" }, noEquals);   // Excel stores some list sources without '='

        var inline = WorkbookInputs.AllowedValues(new WorkbookValidation("D5", "list", "\"Yes,No\"", null, null), "Coverage", sheets);
        Assert.Equal(new[] { "Yes", "No" }, inline);
        Assert.Null(WorkbookInputs.AllowedValues(new WorkbookValidation("E5", "decimal", ">0", null, null), "1. Dashboard", sheets));
        Assert.Null(WorkbookInputs.AllowedValues(new WorkbookValidation("E9", "list", "=Missing!$A$1:$A$2", null, null), "1. Dashboard", sheets));
    }

    /// <summary>Local only: writes the note for every module of the workbook in ASTRA_WB to ASTRA_WB + ".inputs.md".</summary>
    [Fact]
    public void Local_describe_of_a_real_workbook()
    {
        var path = Environment.GetEnvironmentVariable("ASTRA_WB");
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        var bytes = System.IO.File.ReadAllBytes(path);
        var surface = WorkbookSurface.Read(bytes, Path.GetFileName(path));
        var sb = new StringBuilder();
        foreach (var reach in surface.CodeReach)
            { var sh = WorkbookInputs.SheetsOf(surface, reach.Module); sb.AppendLine($"# module {reach.Module} (sheets: {string.Join(", ", sh)})").AppendLine(WorkbookInputs.Describe(bytes, surface, sh) ?? "(none)").AppendLine(); }
        System.IO.File.WriteAllText(path + ".inputs.md", sb.ToString());
    }

    [Fact]
    public void Something_that_is_not_a_workbook_reads_as_nothing()
    {
        Assert.Empty(WorkbookInputs.ReadSheets(Encoding.UTF8.GetBytes("not a zip"), new[] { "Sheet1" }));
    }
}
