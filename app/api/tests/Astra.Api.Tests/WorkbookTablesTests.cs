using System.IO.Compression;
using System.Text;
using Astra.Api.Office;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Api.Tests;

/// <summary>A workbook's lookup sheets are copied as data, so a generated package never has to guess the numbers.</summary>
public class WorkbookTablesTests
{
    private readonly ITestOutputHelper _out;
    public WorkbookTablesTests(ITestOutputHelper output) => _out = output;

    private static byte[] Book(params (string name, string state, string cells)[] sheets)
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
                string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.name}\" sheetId=\"{i + 1}\" state=\"{s.state}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((s, i) => $"<Relationship Id=\"rId{i + 1}\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) + "</Relationships>");
            Put("xl/sharedStrings.xml", $"<sst {ns}><si><t>Age</t></si><si><t>Rate</t></si></sst>");
            for (var i = 0; i < sheets.Length; i++)
                Put($"xl/worksheets/sheet{i + 1}.xml", $"<worksheet {ns}><sheetData>{sheets[i].cells}</sheetData></worksheet>");
        }
        return ms.ToArray();
    }

    /// <summary>A 12 × 3 block of constants starting at row 4, with a header in row 3.</summary>
    private static string RateBlock()
    {
        var sb = new StringBuilder("<row r=\"3\"><c r=\"A3\" t=\"s\"><v>0</v></c><c r=\"B3\" t=\"s\"><v>1</v></c></row>");
        for (var r = 4; r < 16; r++)
            sb.Append($"<row r=\"{r}\"><c r=\"A{r}\"><v>{r + 14}</v></c><c r=\"B{r}\"><v>0.{r}1</v></c><c r=\"C{r}\"><v>{r}.5</v></c></row>");
        return sb.ToString();
    }

    [Fact]
    public void A_sheet_of_numeric_constants_is_a_table_and_keeps_its_cell_positions()
    {
        var tables = WorkbookTables.Read(Book(("Male Nonsmoker", "hidden", RateBlock())));
        var t = Assert.Single(tables);
        Assert.Equal("Male Nonsmoker", t.Sheet);
        Assert.Equal("hidden", t.State);
        Assert.Equal("male_nonsmoker", t.Slug);
        Assert.Equal(15, t.Rows);                       // rows 1-3 are empty/header, data runs to row 15
        Assert.Equal("Age", t.Grid[2][0]);              // header text from the shared strings
        Assert.Equal(18.0, t.Grid[3][0]);               // A4 is grid[3][0]: row 4
        Assert.Equal(0.41, t.Grid[3][1]);
    }

    [Fact]
    public void A_form_sheet_of_formulas_or_a_tiny_sheet_is_not_a_table()
    {
        var formulas = string.Concat(Enumerable.Range(1, 30).Select(i => $"<row r=\"{i}\"><c r=\"A{i}\"><f>B{i}*2</f><v>{i}</v></c><c r=\"B{i}\"><v>{i}</v></c></row>"));
        var tiny = "<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"B1\"><v>2</v></c></row>";
        Assert.Empty(WorkbookTables.Read(Book(("Calculator", "visible", formulas), ("Notes", "visible", tiny))));
    }

    [Fact]
    public void The_json_keeps_row_order_and_the_prompt_text_shows_real_rows_and_the_offset_rule()
    {
        var t = Assert.Single(WorkbookTables.Read(Book(("Rates", "visible", RateBlock()))));
        var json = WorkbookTables.ToJson(t);
        Assert.Contains("\"sheet\":\"Rates\"", json);
        Assert.Contains("\"grid\":[[null,null,null],[null,null,null],[\"Age\",\"Rate\",null],[18,0.41,4.5]", json);

        var prompt = WorkbookTables.DescribeForPrompt(new[] { t });
        Assert.Contains("name \"rates\"", prompt);
        Assert.Contains("row 4: 18, 0.41, 4.5", prompt);
        Assert.Contains("from app.workbook_data import table, cell", prompt);
        Assert.Contains("table(name)[3 + r][c]", prompt);
    }

    [Fact]
    public void The_loader_reads_the_grid_key_of_the_data_file_and_is_plain_python()
    {
        Assert.Equal("backend/app/workbook_data.py", WorkbookTables.LoaderPath);
        Assert.Contains("[\"grid\"]", WorkbookTables.LoaderSource);
        Assert.Contains("def cell(", WorkbookTables.LoaderSource);
        Assert.Contains("from __future__ import annotations", WorkbookTables.LoaderSource);
    }

    [Fact]
    public void Something_that_is_not_a_workbook_gives_no_tables() =>
        Assert.Empty(WorkbookTables.Read(new byte[] { 1, 2, 3, 4 }));

    /// <summary>Local only: the unlicensed workbooks stay outside the repo. Set ASTRA_LOCAL_WORKBOOKS to a folder.</summary>
    [Fact]
    public void Local_report_of_the_tables_found_in_real_workbooks()
    {
        var dir = Environment.GetEnvironmentVariable("ASTRA_LOCAL_WORKBOOKS");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.xlsm", SearchOption.AllDirectories))
        {
            var tables = WorkbookTables.Read(File.ReadAllBytes(f));
            _out.WriteLine($"{Path.GetFileName(f)}: {tables.Count} table(s)");
            foreach (var t in tables) _out.WriteLine($"  {t.Sheet} [{t.State}] {t.Rows}x{t.Columns}, {t.NumericCells} numeric");
        }
    }
}
