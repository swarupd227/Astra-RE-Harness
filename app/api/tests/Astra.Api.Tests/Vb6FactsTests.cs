using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>VB6 forms, classes and modules state their screens, libraries, SQL, settings and files as facts.</summary>
public class Vb6FactsTests
{
    private const string Form = """
        VERSION 5.00
        Object = "{1C0489F8-9EFD-423D-887A-315387F18C8F}#1.0#0"; "vsflex8l.ocx"
        Object = "{F9043C88-F6F2-101A-A3C9-08002B2F49FB}#1.2#0"; "comdlg32.ocx"
        Begin VB.Form frmOrders
           Caption         =   "Order entry"
           ClientHeight    =   3000
           Begin VSFlex8LCtl.VSFlexGrid grdLines
              Height          =   2000
           End
           Begin VB.CommandButton cmdSave
              Caption         =   "&Save"
           End
           Begin VB.CommandButton cmdClose
              Caption         =   "Close"
           End
        End
        Attribute VB_Name = "frmOrders"
        Attribute VB_GlobalNameSpace = False
        Option Explicit
        Private Sub cmdSave_Click()
            Dim sSql As String
            sSql = "INSERT INTO OrderLines (OrderId, Qty) " & _
                   "VALUES (" & mlOrder & ", " & grdLines.TextMatrix(1, 1) & ")"
            Call SaveSetting("Orders", "Window", "LastOrder", CStr(mlOrder))
            frmTotals.Show vbModal
            ' frmNeverOpened.Show
        End Sub
        """;

    private const string Totals = """
        VERSION 5.00
        Begin VB.Form frmTotals
           Caption         =   "Totals"
        End
        Attribute VB_Name = "frmTotals"
        """;

    private const string Class = """
        VERSION 1.0 CLASS
        BEGIN
          MultiUse = -1  'True
        END
        Attribute VB_Name = "clsPricing"
        Option Explicit
        Private Declare Function GetTickCount Lib "kernel32" () As Long
        Public Function Price(ByVal lId As Long) As Currency
            Dim rs As Object
            Set rs = db.OpenRecordset("SELECT Price FROM Products WHERE Id = " & lId)
            sPath = GetSetting(App.Title, "Paths", sKey)
            Open App.Path & "\rates.dat" For Input As #1
            Set xl = CreateObject("Excel.Application")
            frmTotals.Show
            MsgBox "Update the schedule first, then select the rows from the grid."
            sKey = GetSetting(AppName:="Pricing", Section:="Config", Key:="LastUser")
        End Function
        Private Declare Function GetPrivateProfileString Lib "kernel32" Alias "GetPrivateProfileStringA" (ByVal sSection As String, ByVal sKey As String) As Long
        """;

    private static IReadOnlyDictionary<string, CodeFileFacts> ReadAll() =>
        Vb6Facts.Read([new("Forms/Orders.frm", Form), new("Forms/Totals.frm", Totals), new("Classes/Pricing.cls", Class)]);

    [Fact]
    public void A_form_is_a_screen_with_its_caption_controls_and_activex_libraries()
    {
        var facts = ReadAll()["Forms/Orders.frm"].Facts;
        var screen = facts.Entities.Single(e => e.Type == Vb6Facts.ScreenType);
        Assert.Equal("frmOrders", screen.Name);
        Assert.Equal("Order entry", screen.Attrs!["caption"]);
        Assert.Equal(3, screen.Attrs!["controls"]);
        Assert.Contains(facts.Entities, e => e.Type == Vb6Facts.LibraryType && e.Name == "vsflex8l.ocx");
        Assert.Contains(facts.Links, l => l.Relation == EntityVocabulary.References && l.To.Name == "comdlg32.ocx");
    }

    [Fact]
    public void Sql_built_across_continued_lines_names_its_table()
    {
        var links = ReadAll()["Forms/Orders.frm"].Facts.Links;
        var w = links.Single(l => l.Relation == EntityVocabulary.Writes && l.To.Type == EntityVocabulary.Table);
        Assert.Equal("OrderLines", w.To.Name);
        Assert.Equal(EntityVocabulary.Medium, w.Confidence);
        var r = ReadAll()["Classes/Pricing.cls"].Facts.Links.Single(l => l.Relation == EntityVocabulary.Reads && l.To.Type == EntityVocabulary.Table);
        Assert.Equal("Products", r.To.Name);
    }

    [Fact]
    public void Opening_a_screen_is_a_link_and_commented_code_is_not()
    {
        var links = ReadAll()["Forms/Orders.frm"].Facts.Links.Where(l => l.Relation == Vb6Facts.Opens).ToList();
        var open = Assert.Single(links);
        Assert.Equal("frmTotals", open.To.Name);
        Assert.Equal(true, open.Attrs!["modal"]);
        Assert.Contains(ReadAll()["Classes/Pricing.cls"].Facts.Links, l => l.Relation == Vb6Facts.Opens && l.To.Name == "frmTotals");
    }

    [Fact]
    public void Settings_files_dlls_and_com_objects_are_found()
    {
        var cls = ReadAll()["Classes/Pricing.cls"].Facts;
        Assert.Equal("clsPricing", cls.Entities.Single(e => e.Type == EntityVocabulary.Class).Name);
        Assert.Contains(cls.Links, l => l.To.Type == Vb6Facts.LibraryType && l.To.Name == "kernel32.dll");
        Assert.Contains(cls.Links, l => l.To.Type == Vb6Facts.LibraryType && l.To.Name == "Excel.Application");
        Assert.Contains(cls.Links, l => l.Relation == EntityVocabulary.Reads && l.To.Type == EntityVocabulary.File && l.To.Name == "rates.dat");
        var settings = cls.Links.Where(l => l.To.Type == EntityVocabulary.ConfigKey).ToList();
        Assert.Equal(2, settings.Count);   // the Declare line is a signature, not a setting
        Assert.Equal(EntityVocabulary.Medium, settings.Single(s => s.To.Name == "registry:{App.Title}/Paths/{sKey}").Confidence);
        Assert.Equal(EntityVocabulary.High, settings.Single(s => s.To.Name == "registry:Pricing/Config/LastUser").Confidence);
        // A message that starts with "Update" is not SQL.
        Assert.Equal(["Products"], cls.Links.Where(l => l.To.Type == EntityVocabulary.Table).Select(l => l.To.Name));

        var saved = ReadAll()["Forms/Orders.frm"].Facts.Links.Single(l => l.To.Type == EntityVocabulary.ConfigKey);
        Assert.Equal(EntityVocabulary.Writes, saved.Relation);
        Assert.Equal("registry:Orders/Window/LastOrder", saved.To.Name);
    }

    [Fact]
    public void Every_fact_points_at_a_line()
    {
        foreach (var (_, f) in ReadAll())
        {
            Assert.All(f.Facts.Links, l => Assert.Matches(@"^L\d+-\d+$", l.Locator));
            Assert.All(f.Facts.Entities, e => Assert.Matches(@"^L\d+-\d+$", e.Locator));
        }
    }

    /// <summary>For a look at a real project: set VB6_FACTS_DIR to a folder of .frm/.cls/.bas files; a summary is written next to it.</summary>
    [Fact]
    public void A_real_project_can_be_summarised_on_request()
    {
        var dir = Environment.GetEnvironmentVariable("VB6_FACTS_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(Vb6Facts.IsVb6)
            .Select(p => new CodeSource(Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllText(p, System.Text.Encoding.Latin1))).ToList();
        var read = Vb6Facts.Read(files);
        var all = read.Values.SelectMany(v => v.Facts.Entities).ToList();
        var links = read.Values.SelectMany(v => v.Facts.Links).ToList();
        var lines = new List<string>
        {
            $"files {files.Count}; entities {all.Count}; links {links.Count}",
            "entities by type: " + string.Join(", ", all.GroupBy(e => e.Type).Select(g => $"{g.Key}={g.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()}")),
            "links by relation: " + string.Join(", ", links.GroupBy(l => $"{l.Relation}->{l.To.Type}").Select(g => $"{g.Key}={g.Count()}")),
            "tables: " + string.Join(", ", links.Where(l => l.To.Type == EntityVocabulary.Table).Select(l => l.To.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order()),
            "libraries: " + string.Join(", ", all.Where(e => e.Type == Vb6Facts.LibraryType).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order()),
            "settings: " + string.Join(", ", links.Where(l => l.To.Type == EntityVocabulary.ConfigKey).Select(l => l.To.Name).Distinct().Order().Take(40)),
            "files: " + string.Join(", ", links.Where(l => l.To.Type == EntityVocabulary.File).Select(l => l.To.Name).Distinct().Order().Take(40)),
            "opens: " + string.Join("; ", links.Where(l => l.Relation == Vb6Facts.Opens).Select(l => $"{l.From.Name}->{l.To.Name}").Take(80)),
        };
        lines.AddRange(read.Where(r => r.Value.Notes.Count > 0).Select(r => $"note {r.Key}: {string.Join(" ", r.Value.Notes)}"));
        File.WriteAllLines(Path.Combine(dir, "..", "vb6-facts-summary.txt"), lines);
    }
}
