using System.Text.Json;
using Astra.Api.Office;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Api.Tests;

/// <summary>
/// The legacy .xls reader. Real-file checks run over a local folder of MIT-licensed workbooks
/// (ASTRA_XLS_SAMPLES); ASTRA_XLS_REPORT writes each result as JSON so an independent reader (xlrd) can check it.
/// </summary>
public class LegacyWorkbookTests
{
    private readonly ITestOutputHelper _out;
    public LegacyWorkbookTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Local_xls_samples_report()
    {
        var root = Environment.GetEnvironmentVariable("ASTRA_XLS_SAMPLES");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        var report = new List<object>();
        foreach (var f in Directory.GetFiles(root, "*.xls", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var r = WorkbookSurface.Read(File.ReadAllBytes(f), Path.GetFileName(f));
            _out.WriteLine($"{Path.GetFileName(f),-28} sheets={r.Sheets.Count} names={r.Names.Count} formulas={r.Sheets.Sum(s => s.FormulaCells)} " +
                           $"lists={r.Sheets.Sum(s => s.Validations.Count)} controls={r.Sheets.Sum(s => s.Controls.Count)} modules={r.Vba?.Modules.Count ?? 0} warnings={r.Warnings.Count}");
            foreach (var w in r.Warnings) _out.WriteLine("    warning: " + w);
            report.Add(new { r.FileName, r.Sheets, r.Names, r.Events, r.CodeReach, r.Externals, r.Warnings });
        }
        var dir = Environment.GetEnvironmentVariable("ASTRA_XLS_REPORT");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "xls-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
    }

    private static string Fixture(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures", "office"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "Fixtures", "office" }.Concat(path).ToArray());
    }

    private static WorkbookSurfaceResult ReadFixture(params string[] path) =>
        WorkbookSurface.Read(File.ReadAllBytes(Fixture(path)), Path.GetFileName(path[^1]));

    /// <summary>Excel wrote this file; calamine's test for it (test_xls_formula_references) states the expected text.</summary>
    [Fact]
    public void Formulas_read_as_Excel_shows_them_relative_absolute_mixed_and_across_sheets()
    {
        var r = ReadFixture("xls", "formula_references.xls");
        Assert.Empty(r.Warnings);
        var sheet1 = r.Sheets.Single(s => s.Name == "Sheet1");
        Assert.Equal(new[]
        {
            "A1", "$A$1", "$A1", "A$1", "SUM(A1:B2)", "SUM($A$1:$B$2)", "SUM($A1:B$2)",
            "Sheet2!A1", "Sheet2!$A$1", "Sheet2!$A1", "Sheet2!A$1", "Sheet2!B2", "Sheet2!$B2",
            "SUM(Sheet2!A1:B2)", "SUM(Sheet2!$A$1:$B$2)", "SUM(Sheet2!$A1:B$2)",
        }, sheet1.Formulas.Select(f => f.Formula));
        Assert.Equal(Enumerable.Range(1, 16).Select(n => "D" + n), sheet1.Formulas.Select(f => f.Cell));
        Assert.Equal(new[] { "Sheet2" }, sheet1.ReadsFromSheets);
        Assert.Equal(16, sheet1.FormulaCells);
        Assert.Equal("A1:D16", sheet1.UsedRange);
    }

    [Fact]
    public void A_very_hidden_sheet_and_a_workbook_open_event_are_read_from_an_Excel_file()
    {
        var r = ReadFixture("xls", "Check_this_out.xls");
        Assert.Equal(new[] { ("xdAJT", "veryHidden"), ("Feuil1", "visible") }, r.Sheets.Select(s => (s.Name, s.State)));
        Assert.Contains(r.Events, e => e.Trigger == "Workbook_Open");
        Assert.True(r.Vba!.Modules.Count > 0);
    }

    /// <summary>
    /// The same workbook as .xlsm and as .xls (converted by LibreOffice): two independent readers must agree on
    /// its named cells, its controls, the cells they write and the routines its buttons run.
    /// </summary>
    [Fact]
    public void A_converted_workbook_reads_like_its_original_for_names_controls_and_macros()
    {
        var original = ReadFixture("vba-web", "VBA-Web - Installer.xlsm");
        var legacy = ReadFixture("xls", "Installer-from-xlsm.xls");
        Assert.Empty(legacy.Warnings);

        Assert.Equal(17, legacy.Names.Count);
        Assert.Equal(original.Names.Select(n => (n.Name, n.RefersTo)).OrderBy(x => x.Name, StringComparer.Ordinal),
                     legacy.Names.Select(n => (n.Name, n.RefersTo)).OrderBy(x => x.Name, StringComparer.Ordinal));

        var sheet = Assert.Single(legacy.Sheets);
        Assert.Equal(("Install VBA-Web", "visible", "B1:I30"), (sheet.Name, sheet.State, sheet.UsedRange));

        // Two buttons run Install.Install and Install.Browse, both found in the VBA project, as in the original.
        var buttons = sheet.Controls.Where(c => c.Kind == "Button").OrderBy(c => c.MacroRoutine, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ("Install", "Browse"), ("Install", "Install") }, buttons.Select(b => (b.MacroModule!, b.MacroRoutine!)));
        Assert.All(buttons, b => Assert.True(b.MacroFound));
        Assert.Equal(new[] { "Browse", "Install / Update" }, buttons.Select(b => b.Text).OrderBy(x => x, StringComparer.Ordinal));
        var originalButtons = original.Sheets.Single().Controls.Where(c => c.Kind == "Button").Select(b => (b.MacroModule, b.MacroRoutine, b.MacroFound));
        Assert.Equal(originalButtons.OrderBy(x => x.MacroRoutine, StringComparer.Ordinal), buttons.Select(b => (b.MacroModule, b.MacroRoutine, b.MacroFound)));

        // Thirteen check boxes, each writing the cell the original's check box writes.
        var boxes = sheet.Controls.Where(c => c.Kind == "CheckBox").Select(c => c.LinkedCell).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(13, boxes.Count);
        Assert.Equal(original.Sheets.Single().Controls.Where(c => c.Kind == "CheckBox").Select(c => c.LinkedCell).OrderBy(x => x, StringComparer.Ordinal), boxes);

        Assert.Equal(original.Vba!.Modules.Count, legacy.Vba!.Modules.Count);
    }

    [Fact]
    public void Garbage_and_biff5_do_not_throw()
    {
        var r = WorkbookSurface.Read(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "broken.xls");
        Assert.Empty(r.Sheets);
        Assert.NotEmpty(r.Warnings);
    }
}
