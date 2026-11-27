using Astra.Api.Assembly;
using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Faithful 1:1 unit packages become one .NET application: stubs of converted units go, library stubs merge.</summary>
public class DotnetFaithfulAssemblerTests
{
    private static readonly string ArchetypeDir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes", "dotnet10-faithful", "faithful-vb6-unit"));

    private static Dictionary<string, string> Shell()
    {
        var files = new Dictionary<string, string>();
        foreach (var p in new[] { "Faithful.Unit.csproj", "tests/Faithful.Unit.Tests.csproj", "src/Provenance.cs", "src/Vb6Runtime/Controls.cs", "src/Vb6Runtime/Interaction.cs", "tests/UnitTests.cs" })
            files[p] = File.ReadAllText(Path.Combine(ArchetypeDir, p));
        return files;
    }

    private const string ModRates = """
        namespace Faithful.Vb6App;
        using Faithful.Provenance;
        using Faithful.Vb6;
        using Faithful.External.AP_Main;
        [SourceUnit("Modules/modRates.bas")]
        public static partial class modRates
        {
            public static string gsDataPath = "";
            [SourceRoutine("Modules/modRates.bas", "Total", 3, 9)]
            public static int Total(int a, int b) => new clsLogin().Check(a) ? a + b : 0;
            [SourceRoutine("Modules/modRates.bas", "ShowRates", 10, 12)]
            public static void ShowRates() => Forms.Get<frmRates>().Show(Vb.vbModal);
        }
        """;

    private const string FrmRates = """
        namespace Faithful.Vb6App;
        using Faithful.Provenance;
        using Faithful.Vb6;
        using Faithful.External.AP_Main;
        using static Faithful.Vb6App.modRates;
        [SourceUnit("Forms/frmRates.frm")]
        public partial class frmRates : VbForm
        {
            public VbTextBox txtTotal = new() { Name = "txtTotal" };
            public VbCommandButton cmdAdd = new() { Name = "cmdAdd", Caption = "Add" };
            [SourceRoutine("Forms/frmRates.frm", "cmdAdd_Click", 20, 24)]
            public void cmdAdd_Click() { if (new clsLogin().Ready) txtTotal.Text = Total(1, 2).ToString(); }
        }
        """;

    private const string View = """
        @page "/forms/frmRates"
        @namespace Faithful.Vb6App
        @using Faithful.Vb6
        @inherits VbFormView<frmRates>
        <input style="@Layout.Style(Form.txtTotal)" @bind="Form.txtTotal.Text" />
        <button @onclick="Form.cmdAdd_Click">Add</button>
        """;

    private static UnitPackage Unit(string name, string path, Dictionary<string, string> files, DateTimeOffset at) =>
        new(Guid.NewGuid(), name, path, Guid.NewGuid(), files, at);

    private static IReadOnlyList<UnitPackage> Packages()
    {
        var t0 = DateTimeOffset.UtcNow;
        var mod = Shell();
        mod["src/modRates.cs"] = ModRates;
        mod["src/Stubs/Units/frmRates.cs"] = "// TODO(faithful): stub for frmRates\nnamespace Faithful.Vb6App;\nusing Faithful.Vb6;\npublic partial class frmRates : VbForm { }\n";
        mod["src/Stubs/AP_Main/clsLogin.cs"] = "// TODO(faithful): stub for AP_Main.clsLogin\nnamespace Faithful.External.AP_Main;\npublic partial class clsLogin\n{\n    public bool Check(int id) => throw new NotImplementedException();\n}\n";
        mod["tests/modRatesClaimTests.cs"] = "namespace Faithful.Tests;\nusing Faithful.Vb6App;\nusing Xunit;\npublic class ClaimTests\n{\n    [Fact] public void INV_1_path_starts_empty() => Assert.Equal(\"\", modRates.gsDataPath);\n}\n";

        var frm = Shell();
        frm["src/frmRates.cs"] = FrmRates;
        frm["src/View_frmRates.razor"] = View;
        frm["src/Stubs/Units/modRates.cs"] = "namespace Faithful.Vb6App;\npublic static partial class modRates\n{\n    public static int Total(int a, int b) => throw new NotImplementedException();\n}\n";
        frm["src/Stubs/AP_Main/clsLogin.cs"] = "// TODO(faithful): stub for AP_Main.clsLogin\nnamespace Faithful.External.AP_Main;\npublic partial class clsLogin\n{\n    public bool Ready { get; set; }\n    public bool Check(int id) => throw new NotImplementedException();\n}\n";
        frm["tests/frmRatesClaimTests.cs"] = "namespace Faithful.Tests;\nusing Faithful.Vb6App;\nusing Xunit;\npublic class ClaimTests\n{\n    [Fact] public void SE_1_add_needs_login() { var f = new frmRates(); f.cmdAdd_Click(); Assert.Equal(\"\", f.txtTotal.Text); }\n}\n";

        // The module was converted for two routines (the same package twice), the form for one.
        return
        [
            Unit("Total", "Modules/modRates.bas", mod, t0),
            Unit("ShowRates", "Modules/modRates.bas", mod, t0.AddMinutes(1)),
            Unit("cmdAdd_Click", "Forms/frmRates.frm", frm, t0),
        ];
    }

    [Fact]
    public void Units_are_taken_once_and_stubs_of_converted_units_are_dropped()
    {
        var a = new DotnetFaithfulAssembler().Assemble(Packages(), new AssemblyOptions("Rates"));
        Assert.Equal(2, a.Report.Units.Count);
        Assert.Contains("src/modRates.cs", a.Files.Keys);
        Assert.Contains("src/frmRates.cs", a.Files.Keys);
        Assert.DoesNotContain(a.Files.Keys, p => p.StartsWith("src/Stubs/Units/"));
        Assert.Single(a.Files.Keys, p => p == "src/Vb6Runtime/Controls.cs");
        Assert.Contains(a.Report.Shared, s => s.Location == "src/Vb6Runtime/Controls.cs");
        Assert.Contains(a.Report.Notes, n => n.Contains("3 routine packages came from 2 source units"));
    }

    [Fact]
    public void Library_stubs_are_merged_member_by_member()
    {
        var a = new DotnetFaithfulAssembler().Assemble(Packages(), new AssemblyOptions("Rates"));
        var stub = a.Files["src/Stubs/AP_Main/clsLogin.cs"];
        Assert.Contains("Ready", stub);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(stub, @"bool Check\(").Cast<object>());
        Assert.Contains("namespace Faithful.External.AP_Main;", stub);
        Assert.Contains("TODO(faithful)", stub);
    }

    [Fact]
    public void Each_units_tests_get_their_own_namespace_and_the_app_hosts_the_pages()
    {
        var a = new DotnetFaithfulAssembler().Assemble(Packages(), new AssemblyOptions("Rates"));
        Assert.Contains("namespace Faithful.Tests.modRates;", a.Files["tests/Units/modRates/modRatesClaimTests.cs"]);
        Assert.Contains("namespace Faithful.Tests.frmRates;", a.Files["tests/Units/frmRates/frmRatesClaimTests.cs"]);
        Assert.Contains("Microsoft.NET.Sdk.Web", a.Files["Faithful.App.csproj"]);
        Assert.Contains(@"..\Faithful.App.csproj", a.Files["tests/Faithful.App.Tests.csproj"]);
        Assert.Contains("forms/frmRates", a.Files["src/Host/Home.razor"]);
        Assert.Contains("MapRazorComponents<HostApp>", a.Files["src/Host/Program.cs"]);
        Assert.Contains("| frmRates |", a.Files["ASSEMBLY.md"].Replace("cmdAdd_Click", "frmRates"));
        Assert.NotNull(ModuleAssemblers.For(FaithfulConversion.Stack));
    }

    [Fact]
    public void The_same_packages_give_the_same_files()
    {
        var a = new DotnetFaithfulAssembler().Assemble(Packages(), new AssemblyOptions("Rates"));
        var b = new DotnetFaithfulAssembler().Assemble(Packages(), new AssemblyOptions("Rates"));
        Assert.Equal(a.Files.Keys, b.Files.Keys);
        foreach (var k in a.Files.Keys) Assert.Equal(a.Files[k], b.Files[k]);

        // For a look at (and a build of) the assembled project: set ASSEMBLY_SAMPLE_DIR.
        if (Environment.GetEnvironmentVariable("ASSEMBLY_SAMPLE_DIR") is { Length: > 0 } dir)
            foreach (var (path, content) in a.Files)
            {
                var abs = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllText(abs, content);
            }
    }

    private static string FindInSourceTree(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(relative);
    }
}
