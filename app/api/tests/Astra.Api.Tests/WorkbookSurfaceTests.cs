using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Astra.Api.Office;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Api.Tests;

public class WorkbookSurfaceTests
{
    private readonly ITestOutputHelper _out;
    public WorkbookSurfaceTests(ITestOutputHelper output) => _out = output;

    private const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    private const string RelNs = "xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"";
    private const string RelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private static byte[] Package(Dictionary<string, string> parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    /// <summary>A small rating template written by hand, so every expected fact is visible in the test.</summary>
    private static byte[] RatingTemplate() => Package(new()
    {
        ["xl/workbook.xml"] = $"""
            <workbook {Ns}><sheets>
              <sheet name="Quote" sheetId="1" r:id="rId1"/>
              <sheet name="Rates" sheetId="2" state="veryHidden" r:id="rId2"/>
            </sheets><definedNames>
              <definedName name="Age">Quote!$B$2</definedName>
              <definedName name="Cover">Quote!$B$3</definedName>
              <definedName name="RateTable">Rates!$A$1:$B$10</definedName>
              <definedName name="_xlnm.Print_Area" localSheetId="0">Quote!$A$1:$D$20</definedName>
            </definedNames></workbook>
            """,
        ["xl/_rels/workbook.xml.rels"] = $"""
            <Relationships {RelNs}>
              <Relationship Id="rId1" Type="{RelType}worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="{RelType}worksheet" Target="worksheets/sheet2.xml"/>
              <Relationship Id="rId9" Type="{RelType}sharedStrings" Target="sharedStrings.xml"/>
            </Relationships>
            """,
        ["xl/sharedStrings.xml"] = $"""<sst {Ns}><si><t>Age next birthday</t></si><si><t>Sum assured</t></si><si><t>Smoker?</t></si><si><t>Premium</t></si></sst>""",
        ["xl/worksheets/sheet1.xml"] = $$"""
            <worksheet {{Ns}} xmlns:x14="http://schemas.microsoft.com/office/spreadsheetml/2009/9/main" xmlns:xm="http://schemas.microsoft.com/office/excel/2006/main">
              <sheetPr codeName="shQuote"/><dimension ref="A1:D6"/>
              <sheetData>
                <row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2"><v>40</v></c></row>
                <row r="3"><c r="A3" t="s"><v>1</v></c><c r="B3"><v>100000</v></c></row>
                <row r="4"><c r="A4" t="s"><v>2</v></c><c r="B4" t="str"><v>No</v></c></row>
                <row r="5"><c r="A5" t="s"><v>3</v></c><c r="B5"><f>VLOOKUP(Age,RateTable,2,FALSE)*Cover/1000</f><v>120</v></c></row>
                <row r="6"><c r="C6"><f t="shared" ref="C6:C8" si="0">'Rates'!B1*2</f><v>1</v></c></row>
                <row r="7"><c r="C7"><f t="shared" si="0"/><v>1</v></c></row>
                <row r="9"><c r="C9"><f>MAX(rates[rate])</f><v>0.9</v></c></row>
              </sheetData>
              <dataValidations count="2"><dataValidation type="list" sqref="B4" promptTitle="Smoker"><formula1>"Yes,No"</formula1></dataValidation>
                <dataValidation type="whole" operator="between" sqref="B3"><formula1>1000</formula1><formula2>5000000</formula2></dataValidation></dataValidations>
              <legacyDrawing r:id="rId1"/>
              <extLst><ext uri="{CCE6A557-97BC-4b89-ADB6-D9C93CAAB3DF}"><x14:dataValidations count="1">
                <x14:dataValidation type="list"><x14:formula1><xm:f>Rates!$A$1:$A$10</xm:f></x14:formula1><xm:sqref>B2</xm:sqref></x14:dataValidation>
              </x14:dataValidations></ext></extLst>
            </worksheet>
            """,
        ["xl/worksheets/_rels/sheet1.xml.rels"] = $"""
            <Relationships {RelNs}><Relationship Id="rId1" Type="{RelType}vmlDrawing" Target="../drawings/vmlDrawing1.vml"/></Relationships>
            """,
        // Excel's VML is not well-formed XML (the bare <br>); the reader must cope.
        ["xl/drawings/vmlDrawing1.vml"] = """
            <xml xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:x="urn:schemas-microsoft-com:office:excel">
             <v:shape id="_x0000_s1025" o:spid="_x0000_s1025" type="#_x0000_t201">
              <v:textbox><div><font>Get<br>quote</font></div></v:textbox>
              <x:ClientData ObjectType="Button"><x:FmlaMacro>[0]!Pricing.Quote</x:FmlaMacro></x:ClientData>
             </v:shape>
             <v:shape id="Check Box 2" o:spid="_x0000_s1026" type="#_x0000_t201">
              <x:ClientData ObjectType="Checkbox"><x:Checked>1</x:Checked><x:FmlaLink>$D$4</x:FmlaLink></x:ClientData>
             </v:shape>
             <v:shape id="_x0000_s1027" o:spid="_x0000_s1027" type="#_x0000_t202">
              <x:ClientData ObjectType="Note"><x:Row>1</x:Row></x:ClientData>
             </v:shape>
            </xml>
            """,
        ["xl/worksheets/sheet2.xml"] = $"""
            <worksheet {Ns}><sheetPr codeName="shRates"/><sheetData><row r="1"><c r="A1"><v>18</v></c><c r="B1"><v>0.9</v></c></row></sheetData><tableParts count="1"><tablePart r:id="rId1"/></tableParts></worksheet>
            """,
        ["xl/worksheets/_rels/sheet2.xml.rels"] = $"""
            <Relationships {RelNs}><Relationship Id="rId1" Type="{RelType}table" Target="../tables/table1.xml"/></Relationships>
            """,
        ["xl/tables/table1.xml"] = $"""
            <table {Ns} id="1" name="rates" displayName="rates" ref="A1:B10"><tableColumns count="2"><tableColumn id="1" name="age"/><tableColumn id="2" name="rate"/></tableColumns></table>
            """,
        ["xl/connections.xml"] = $"""
            <connections {Ns}><connection id="1" name="Policy DB" type="1"><dbPr connection="DSN=Policies;UID=svc_rating;PWD=hunter2;" command="select 1"/></connection></connections>
            """,
    });

    [Fact]
    public void Reads_sheets_names_formulas_lists_and_controls_of_a_template()
    {
        var r = WorkbookSurface.Read(RatingTemplate(), "rating.xlsx");
        Assert.Empty(r.Warnings);

        Assert.Equal(new[] { "Quote", "Rates" }, r.Sheets.Select(s => s.Name));
        var quote = r.Sheets[0];
        Assert.Equal("shQuote", quote.CodeName);
        Assert.Equal("veryHidden", r.Sheets[1].State);

        // Excel's own names are left out; a single-cell name is labelled by the text beside it.
        Assert.Equal(new[] { "Age", "Cover", "RateTable" }, r.Names.Select(n => n.Name));
        Assert.Equal("Age next birthday", r.Names.Single(n => n.Name == "Age").Label);

        // A shared formula is listed once, at its first cell, with its range; every cell is counted.
        Assert.Equal(4, quote.FormulaCells);
        Assert.Equal(new[] { "table:rates" }, quote.Formulas.Single(f => f.Cell == "C9").Refs);
        Assert.Equal(new[] { "age", "rate" }, r.Sheets[1].Tables.Single().Columns);
        var premium = quote.Formulas.Single(f => f.Cell == "B5");
        Assert.Equal(new[] { "name:Age", "name:Cover", "name:RateTable" }, premium.Refs);
        var shared = quote.Formulas.Single(f => f.Cell == "C6");
        Assert.Equal("C6:C8", shared.Range);
        Assert.Contains("sheet:Rates", shared.Refs);
        Assert.Equal(new[] { "Rates" }, quote.ReadsFromSheets);

        // Both places Excel keeps validations: the plain list and the x14 one pointing at another sheet.
        var smoker = quote.Validations.Single(v => v.Cells == "B4");
        Assert.Equal(("list", "Yes,No", "Smoker?"), (smoker.Type, smoker.Source, smoker.Label));
        Assert.Equal("1000 to 5000000", quote.Validations.Single(v => v.Cells == "B3").Source);
        var age = quote.Validations.Single(v => v.Cells == "B2");
        Assert.Equal("Rates!$A$1:$A$10", age.Source);

        // Form controls from the VML; the comment shape is not a control.
        Assert.Equal(2, quote.Controls.Count);
        var button = quote.Controls.Single(c => c.Kind == "Button");
        Assert.Equal("Button 1025", button.Name);       // no <controls> entry: kind and shape id, not "_x0000_s1025"
        Assert.Equal(("form", "Get quote", "[0]!Pricing.Quote", "Pricing", "Quote"), (button.Technology, button.Text, button.Macro, button.MacroModule, button.MacroRoutine));
        Assert.False(button.MacroFound);                 // this package carries no VBA, so the target cannot be found
        Assert.Equal("$D$4", quote.Controls.Single(c => c.Kind == "CheckBox").LinkedCell);

        // A connection keeps where it points but never its credentials.
        var db = Assert.Single(r.Externals);
        Assert.Equal(("odbc", "Policy DB"), (db.Kind, db.Name));
        Assert.DoesNotContain("hunter2", db.Target);
        Assert.DoesNotContain("svc_rating", db.Target);
        Assert.Contains("DSN=Policies", db.Target);
    }

    [Fact]
    public void Form_buttons_are_bound_to_the_routines_they_run()
    {
        var path = Path.Combine(FixtureDir(), "vba-web", "VBA-Web - Installer.xlsm");
        var r = WorkbookSurface.Read(File.ReadAllBytes(path), Path.GetFileName(path));
        foreach (var w in r.Warnings) _out.WriteLine("warning: " + w);
        Assert.Empty(r.Warnings);

        var sheet = Assert.Single(r.Sheets);
        Assert.Equal("Install", sheet.CodeName);
        var buttons = sheet.Controls.Where(c => c.Kind == "Button").OrderBy(c => c.MacroRoutine).ToList();
        Assert.Equal(new[] { "Browse", "Install" }, buttons.Select(b => b.MacroRoutine));
        Assert.All(buttons, b => Assert.Equal("Install", b.MacroModule));
        Assert.All(buttons, b => Assert.True(b.MacroFound, $"{b.Macro} should resolve to a routine in the project"));

        var boxes = sheet.Controls.Where(c => c.Kind == "CheckBox").ToList();
        Assert.Equal(13, boxes.Count);
        Assert.All(boxes, b => Assert.Matches(@"^\$C\$\d+$", b.LinkedCell!));
    }

    [Fact]
    public void Garbage_and_documents_that_are_not_workbooks_do_not_throw()
    {
        var r = WorkbookSurface.Read(Encoding.ASCII.GetBytes("not a workbook, just text long enough to look at"), "x.xlsm");
        Assert.Empty(r.Sheets);
        Assert.NotEmpty(r.Warnings);

        var word = Package(new() { ["word/document.xml"] = "<document/>" });
        var r2 = WorkbookSurface.Read(word, "letter.docm");
        Assert.Empty(r2.Sheets);
        Assert.Contains(r2.Warnings, w => w.Contains("not a workbook"));
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures", "office")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Fixtures", "office");
    }

    /// <summary>
    /// Over a local folder of samples that are not committed (ASTRA_VBA_SAMPLES): prints a
    /// summary, and with ASTRA_WORKBOOK_REPORT set writes every result as JSON for a viewer.
    /// </summary>
    [Fact]
    public void Local_samples_surface_report()
    {
        var root = Environment.GetEnvironmentVariable("ASTRA_VBA_SAMPLES");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        var results = new List<WorkbookSurfaceResult>();
        foreach (var f in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => OfficeVba.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f))
        {
            var r = WorkbookSurface.Read(File.ReadAllBytes(f), Path.GetFileName(f));
            results.Add(r);
            var controls = r.Sheets.SelectMany(s => s.Controls).ToList();
            _out.WriteLine($"{r.FileName,-30} sheets={r.Sheets.Count,2} names={r.Names.Count,3} formulas={r.Sheets.Sum(s => s.FormulaCells),6:N0} " +
                           $"lists={r.Sheets.Sum(s => s.Validations.Count),2} controls={controls.Count,2} (bound {controls.Count(c => c.MacroFound)}) " +
                           $"events={r.Events.Count} externals={r.Externals.Count} warnings={r.Warnings.Count}");
            foreach (var c in controls.Where(c => (c.Macro is not null || c.Technology == "activex") && !c.MacroFound))
                _out.WriteLine($"    unbound: {c.Technology} {c.Name} {c.Macro}");
        }
        var report = Environment.GetEnvironmentVariable("ASTRA_WORKBOOK_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            Directory.CreateDirectory(report);
            // Module source is left out of the viewer's data; it is large and already dumped elsewhere.
            var slim = results.Select(r => new
            {
                r.FileName, r.Sheets, r.Names, r.Events, r.CodeReach, r.Externals, r.Warnings,
                Vba = r.Vba is null ? null : new { r.Vba.Name, Modules = r.Vba.Modules.Select(m => new { m.Name, Kind = m.Kind.ToString(), Lines = m.Source.Count(ch => ch == '\n') }) },
            });
            var json = JsonSerializer.Serialize(slim, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(Path.Combine(report, "surface-data.js"), "window.SURFACES = " + json + ";\n");
        }
    }
}
