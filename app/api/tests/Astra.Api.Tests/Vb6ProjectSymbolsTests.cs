using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>A unit converted alone is told where the project's other names live, and parts never declare a member twice.</summary>
public class Vb6ProjectSymbolsTests
{
    private const string ScheduleMod = """
        Attribute VB_Name = "ScheduleMod"
        Option Explicit
        Public moBuildMasterSchedule As clsBuildMasterSchedule
        Public lsaMessage() As String
        Public Const MAX_SECTIONS = 99
        Public Type GradeInfo
            Low As Integer
        End Type
        Private mlHidden As Long
        Public Function Messages(ByVal iNo As Integer) As String
        End Function
        Private Sub Secret()
        End Sub
        """;

    private const string BuildCls = """
        VERSION 1.0 CLASS
        Attribute VB_Name = "clsBuildMasterSchedule"
        Public Function Messages(ByVal iNo As Integer) As String
        End Function
        """;

    private const string Unit = """
        Attribute VB_Name = "clsConflictMatrix"
        Private Sub Run()
            lsaMessage(1) = Messages(3)   ' a module function
            Set moBuildMasterSchedule = New clsBuildMasterSchedule
            If x > MAX_SECTIONS Then Secret
            Dim g As GradeInfo
        End Sub
        """;

    private static Vb6ProjectSymbols Build() => Vb6ProjectSymbols.Build(
        [("Module/ScheduleMod.bas", ScheduleMod), ("Class/Build.cls", BuildCls), ("Class/ConflictMatrix.cls", Unit)],
        [("Messages", "Public Function Messages(ByVal iNo As Integer) As String", "Module/ScheduleMod.bas"),
         ("Secret", "Private Sub Secret()", "Module/ScheduleMod.bas"),
         ("Messages", "Public Function Messages(ByVal iNo As Integer) As String", "Class/Build.cls"),
         ("Run", "Private Sub Run()", "Class/ConflictMatrix.cls")]);

    [Fact]
    public void A_unit_is_told_which_module_owns_each_routine_and_variable_it_uses()
    {
        var text = Build().RenderFor(Unit, "clsConflictMatrix");
        Assert.Contains("module `ScheduleMod`", text);
        Assert.Contains("routine `Messages` — `Public Function Messages(ByVal iNo As Integer) As String`", text);
        Assert.Contains("variable `moBuildMasterSchedule` — `Public moBuildMasterSchedule As clsBuildMasterSchedule`", text);
        Assert.Contains("variable `lsaMessage`", text);
        Assert.Contains("constant `MAX_SECTIONS`", text);
        Assert.Contains("type `GradeInfo`", text);
        Assert.Contains("class `clsBuildMasterSchedule`", text);
        Assert.DoesNotContain("Secret", text);          // private to its module
        Assert.DoesNotContain("mlHidden", text);
        Assert.DoesNotContain("Run", text);             // the unit's own
    }

    [Fact]
    public void A_unit_using_nothing_from_elsewhere_says_so()
    {
        var text = Build().RenderFor("Attribute VB_Name = \"clsX\"\nPrivate Sub A()\nEnd Sub", "clsX");
        Assert.StartsWith("(this unit uses nothing", text);
    }

    [Fact]
    public void Parts_keep_each_member_and_the_class_attributes_once()
    {
        var part2 = new FaithfulConversion.PackageFile("src/clsX.Part2.cs", "csharp", """
            namespace Faithful.Vb6App;
            [SourceUnit("Class/X.cls")]
            public partial class clsX
            {
                public void MasterRoster() { }
                public string FormatWithSpace(string s) => s;
            }
            """, ["INV-1"]);
        var part3 = new FaithfulConversion.PackageFile("src/clsX.Part3.cs", "csharp", """
            namespace Faithful.Vb6App;
            [SourceUnit("Class/X.cls")]
            public partial class clsX
            {
                public void MasterRoster() { }
                public void DoGrid() { }
                public string FormatWithSpace(string s) => s + " ";
            }
            """, []);
        var other = new FaithfulConversion.PackageFile("tests/T.cs", "csharp", "not c# at all {", []);
        var result = FaithfulParts.Dedupe([part2, part3, other]);
        Assert.Equal(part2.Content, result[0].Content);
        Assert.DoesNotContain("MasterRoster", result[1].Content);
        Assert.DoesNotContain("FormatWithSpace", result[1].Content);
        Assert.Contains("DoGrid", result[1].Content);
        Assert.DoesNotContain("SourceUnit", result[1].Content);
        Assert.Equal(other, result[2]);
    }

    /// <summary>For a look at a real project: set VB6_FACTS_DIR (a folder of .frm/.cls/.bas) and VB6_UNIT (a file in it).</summary>
    [Fact]
    public void A_real_projects_symbols_can_be_shown_on_request()
    {
        var dir = Environment.GetEnvironmentVariable("VB6_FACTS_DIR");
        var unit = Environment.GetEnvironmentVariable("VB6_UNIT");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(unit) || !Directory.Exists(dir)) return;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(Evidence.Vb6Facts.IsVb6)
            .Select(p => (Path: Path.GetRelativePath(dir, p).Replace('\\', '/'), Text: File.ReadAllText(p, System.Text.Encoding.Latin1))).ToList();
        var routines = files.SelectMany(f => System.Text.RegularExpressions.Regex.Matches(f.Text,
                @"^\s*((?:Public|Private|Friend)\s+)?(?:Static\s+)?(?:Sub|Function|Property\s+(?:Get|Let|Set))\s+(\w+)[^\r\n]*", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => (m.Groups[2].Value, m.Value.Trim(), f.Path))).ToList();
        var symbols = Vb6ProjectSymbols.Build(files, routines);
        var text = files.First(f => f.Path.EndsWith(unit, StringComparison.OrdinalIgnoreCase)).Text;
        File.WriteAllText(Path.Combine(dir, "..", "vb6-symbols.txt"), symbols.RenderFor(text, FaithfulConversion.Vb6NameOf(text) ?? ""));
    }
}
