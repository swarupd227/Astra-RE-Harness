using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Llm;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Persistence.Entities;
using Xunit;

namespace Astra.Api.Tests;

public class FaithfulConversionTests
{
    [Theory]
    [InlineData("dotnet10-faithful", true)]
    [InlineData("DOTNET10-FAITHFUL", true)]
    [InlineData("dotnet10", false)]
    [InlineData("dotnet8", false)]
    [InlineData(null, false)]
    public void IsFaithful_recognises_only_the_faithful_stack(string? stack, bool expected) =>
        Assert.Equal(expected, FaithfulConversion.IsFaithful(stack));

    [Theory]
    [InlineData("Lib/Core/IdScheduler.pas", "IdScheduler", "Faithful.IdScheduler")]
    [InlineData(@"Lib\Core\IdIOHandler.pas", "IdIOHandler", "Faithful.IdIOHandler")]
    [InlineData("3d-math.pas", "_3d_math", "Faithful._3d_math")]
    [InlineData("", "Unit", "Faithful.Unit")]
    public void Unit_names_become_identifiers_and_namespaces(string path, string cls, string ns)
    {
        var unit = FaithfulConversion.UnitNameOf(path);
        Assert.Equal(cls, FaithfulConversion.ClassNameFor(unit));
        Assert.Equal(ns, FaithfulConversion.NamespaceFor(unit));
    }

    [Fact]
    public void Build_orders_routines_by_line_and_prefers_the_signed_spec()
    {
        var fileId = Guid.NewGuid();
        var a = new Subroutine { Id = Guid.NewGuid(), SourceFileId = fileId, Name = "TIdScheduler.Create", Signature = "constructor Create;", LineStart = 40, LineEnd = 48, CalledSubroutines = JsonDocument.Parse("[\"InitComponent\",\"InitComponent\"]") };
        var b = new Subroutine { Id = Guid.NewGuid(), SourceFileId = fileId, Name = "TIdScheduler.InitComponent", Signature = "procedure InitComponent; override;", LineStart = 20, LineEnd = 30, CalledSubroutines = JsonDocument.Parse("[{\"name\":\"inherited InitComponent\"}]") };
        var c = new Subroutine { Id = Guid.NewGuid(), SourceFileId = fileId, Name = "TIdScheduler.Destroy", Signature = "destructor Destroy; override;", LineStart = 50, LineEnd = 55 };

        var older = new Spec { Id = Guid.NewGuid(), SubroutineId = b.Id, State = "SIGNED", CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), SpecJson = JsonDocument.Parse("{\"invariants\":[{\"id\":\"INV-1\"}]}") };
        var newer = new Spec { Id = Guid.NewGuid(), SubroutineId = b.Id, State = "SIGNED", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), SpecJson = JsonDocument.Parse("{\"invariants\":[{\"id\":\"INV-2\"}],\"edge_cases\":[{\"id\":\"EC-1\"}]}") };
        var superseded = new Spec { Id = Guid.NewGuid(), SubroutineId = a.Id, State = "SUPERSEDED", CreatedAt = DateTimeOffset.UtcNow, SpecJson = JsonDocument.Parse("{\"invariants\":[{\"id\":\"OLD-1\"}]}") };
        var draft = new Spec { Id = Guid.NewGuid(), SubroutineId = a.Id, State = "DRAFT", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1), SpecJson = JsonDocument.Parse("{\"invariants\":[{\"id\":\"DR-1\"}]}") };

        var unit = FaithfulConversion.Build("Lib/Core/IdScheduler.pas", new[] { a, b, c }, new[] { older, newer, superseded, draft });

        Assert.Equal("IdScheduler", unit.UnitName);
        Assert.Equal(new[] { "TIdScheduler.InitComponent", "TIdScheduler.Create", "TIdScheduler.Destroy" }, unit.Routines.Select(r => r.Name));
        Assert.Equal(new[] { "SIGNED", "DRAFT", "none" }, unit.Routines.Select(r => r.SpecState));
        Assert.Equal(newer.Id, unit.Routines[0].SpecId);
        Assert.Equal(new[] { "inherited InitComponent" }, unit.Routines[0].Calls);
        Assert.Equal(new[] { "InitComponent" }, unit.Routines[1].Calls);

        Assert.Single(unit.SignedSpecs);
        Assert.Equal(new[] { "EC-1", "INV-2" }, unit.ClaimIds().OrderBy(x => x));

        using var inv = JsonDocument.Parse(unit.RoutinesJson());
        var first = inv.RootElement[0];
        Assert.Equal("20-30", first.GetProperty("lines").GetString());
        Assert.Equal("SIGNED", first.GetProperty("spec").GetString());
        Assert.False(inv.RootElement[2].TryGetProperty("calls", out _), "routines without calls omit the field");

        using var specs = JsonDocument.Parse(unit.SpecsJson());
        Assert.Equal("TIdScheduler.InitComponent", specs.RootElement[0].GetProperty("routine").GetString());
        Assert.Equal("INV-2", specs.RootElement[0].GetProperty("spec").GetProperty("invariants")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void ParseToolFiles_defaults_language_filters_claims_and_rejects_bad_paths()
    {
        var json = """
        {"files":[
          {"path":"src/IdScheduler.cs","content":"namespace Faithful.IdScheduler;","derivedFromClaimIds":["INV-2","MADE-UP","INV-2"]},
          {"path":"src\\Stubs\\IdGlobal.cs","language":"csharp","content":"// stub"},
          {"path":"../escape.cs","content":"x"},
          {"path":"src/Empty.cs","content":""},
          {"path":"src/IdScheduler.cs","content":"duplicate"}
        ]}
        """;
        var files = FaithfulConversion.ParseToolFiles(json, new HashSet<string> { "INV-2", "EC-1" });

        Assert.Equal(new[] { "src/IdScheduler.cs", "src/Stubs/IdGlobal.cs" }, files.Select(f => f.Path));
        Assert.Equal("csharp", files[0].Language);
        Assert.Equal(new[] { "INV-2" }, files[0].DerivedFromClaimIds);
        Assert.Empty(FaithfulConversion.ParseToolFiles("not json", new HashSet<string>()));
        Assert.Empty(FaithfulConversion.ParseToolFiles(null, new HashSet<string>()));
    }

    [Fact]
    public void MergeWithArchetype_carries_the_build_shell_but_never_the_exemplar()
    {
        var archetype = new ArchetypeRegistry.LoadedArchetype
        {
            Manifest = new ArchetypeRegistry.ArchetypeManifest { Id = "faithful-delphi-unit", TargetStack = FaithfulConversion.Stack },
            ArchetypeDir = "",
            Files = new List<ArchetypeRegistry.LoadedFile>
            {
                new() { Path = "Faithful.Unit.csproj", Language = "xml", Content = "<Project/>" },
                new() { Path = "src/Provenance.cs", Language = "csharp", Content = "// attrs" },
                new() { Path = "src/Unit.cs", Language = "csharp", Content = "// exemplar" },
                new() { Path = "tests/UnitTests.cs", Language = "csharp", Content = "// anchor" },
            },
        };
        var generated = new List<FaithfulConversion.PackageFile>
        {
            new("src/IdScheduler.cs", "csharp", "// unit", new[] { "INV-2" }),
            new("src/Provenance.cs", "csharp", "// model rewrote it", Array.Empty<string>()),
        };

        var merged = FaithfulConversion.MergeWithArchetype(generated, archetype);

        Assert.Equal(new[] { "src/IdScheduler.cs", "src/Provenance.cs", "Faithful.Unit.csproj", "tests/UnitTests.cs" }, merged.Select(f => f.Path));
        Assert.Equal("// model rewrote it", merged[1].Content);
        Assert.DoesNotContain(merged, f => f.Path == FaithfulConversion.ExemplarPath);
    }

    [Fact]
    public void A_repair_shows_the_failed_package_source_but_not_the_shell()
    {
        var files = """
        [
          {"path":"Faithful.Unit.csproj","content":"<Project/>"},
          {"path":"src/Provenance.cs","content":"// attrs"},
          {"path":"src/MVCViewModel.cs","content":"namespace Faithful.MVCViewModel;\npublic class TBlogApplication {}"},
          {"path":"src\\Stubs\\MormotRestCore.cs","content":"namespace Faithful.MormotRestCore;"},
          {"path":"tests/UnitTests.cs","content":"// anchor"}
        ]
        """;
        var section = FaithfulConversion.PreviousPackageSection(files);
        Assert.Contains("## The package that failed", section);
        Assert.Contains("### src/MVCViewModel.cs", section);
        Assert.Contains("### src/Stubs/MormotRestCore.cs", section);
        Assert.DoesNotContain("Faithful.Unit.csproj", section);
        Assert.DoesNotContain("Provenance", section);
        Assert.DoesNotContain("tests/UnitTests.cs", section);
        Assert.Equal("", FaithfulConversion.PreviousPackageSection(null));
        Assert.Equal("", FaithfulConversion.PreviousPackageSection("not json"));
        Assert.Equal("", FaithfulConversion.PreviousPackageSection("[]"));
    }

    [Theory]
    [InlineData("delphi")]
    [InlineData("cpp")]
    [InlineData("fortran-f77")]
    [InlineData("vb6")]
    public void The_faithful_prompt_on_disk_uses_only_variables_the_provider_supplies(string sourceSchema)
    {
        var prompt = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Prompts", sourceSchema, "dotnet10-faithful", "faithful-transform.v1.md"));
        var text = File.ReadAllText(prompt);
        var used = Regex.Matches(text, @"\{\{(\w+)\}\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();

        Assert.NotEmpty(used);
        var unknown = used.Except(FaithfulConversion.PromptVariables).ToArray();
        Assert.True(unknown.Length == 0, $"placeholders with no value: {string.Join(", ", unknown)}");
        Assert.Contains("unitSourceText", used);
        Assert.Contains("unitSpecsJson", used);
        Assert.Contains("mappingTable", used);
        Assert.Contains($"`{FaithfulConversion.ToolName}`", text);
    }

    [Theory]
    [InlineData("delphi", "rtl-mapping.json")]
    [InlineData("cpp", "stl-mapping.json")]
    [InlineData("vb6", "com-progid-registry.json")]
    [InlineData("fortran-f77", null)]
    [InlineData("php", null)]
    public void MappingAssetFileName_matches_each_languages_own_curated_asset(string sourceSchema, string? expected) =>
        Assert.Equal(expected, FaithfulConversion.MappingAssetFileName(sourceSchema));

    [Fact]
    public void Some_faithful_archetype_lists_every_language_with_a_faithful_prompt()
    {
        var archetypeDir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes", "dotnet10-faithful", "faithful-delphi-unit"));
        var compatible = new HashSet<string?>();
        foreach (var dir in Directory.EnumerateDirectories(Directory.GetParent(archetypeDir)!.FullName))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "archetype.json")));
            compatible.UnionWith(manifest.RootElement.GetProperty("compatibleSchemas").EnumerateArray().Select(e => e.GetString()));
        }

        // archetypeDir = .../Llm/Archetypes/dotnet10-faithful/faithful-delphi-unit — three levels up is .../Llm.
        var llmRoot = archetypeDir;
        for (var i = 0; i < 3; i++) llmRoot = Directory.GetParent(llmRoot)!.FullName;
        var promptsDir = Path.Combine(llmRoot, "Prompts");
        var languagesWithFaithfulPrompt = Directory.EnumerateDirectories(promptsDir)
            .Select(Path.GetFileName)
            .Where(schema => File.Exists(Path.Combine(promptsDir, schema!, "dotnet10-faithful", "faithful-transform.v1.md")))
            .ToArray();

        Assert.NotEmpty(languagesWithFaithfulPrompt);
        foreach (var schema in languagesWithFaithfulPrompt)
            Assert.Contains(schema, compatible);
    }

    [Fact]
    public void The_archetype_on_disk_declares_the_shell_and_the_exemplar()
    {
        var dir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes", "dotnet10-faithful", "faithful-delphi-unit"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "archetype.json")));
        var root = manifest.RootElement;
        Assert.Equal(FaithfulConversion.Stack, root.GetProperty("targetStack").GetString());
        Assert.StartsWith("production", root.GetProperty("status").GetString());
        var paths = root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).ToArray();
        Assert.Contains(FaithfulConversion.ExemplarPath, paths);
        Assert.Contains("src/Provenance.cs", paths);
        Assert.Contains(paths, p => p.StartsWith("tests/") && p.EndsWith(".cs"));
        foreach (var p in paths) Assert.True(File.Exists(Path.Combine(dir, p)), $"declared file missing: {p}");
        Assert.Contains("namespace Faithful.Tests;", File.ReadAllText(Path.Combine(dir, "tests", "UnitTests.cs")));
    }

    // ── VB6 ──────────────────────────────────────────────────────────────

    private static FaithfulConversion.UnitContext Vb6Unit(string path, params (string Name, int Start, int End)[] routines) =>
        new(FaithfulConversion.UnitNameOf(path), path, FaithfulConversion.ClassNameFor(FaithfulConversion.UnitNameOf(path)),
            FaithfulConversion.NamespaceFor(FaithfulConversion.UnitNameOf(path)),
            routines.Select(r => new FaithfulConversion.UnitRoutine(Guid.NewGuid(), r.Name, $"Sub {r.Name}()", r.Start, r.End, Array.Empty<string>(), "none", null)).ToList(),
            Array.Empty<FaithfulConversion.UnitSpec>());

    [Fact]
    public void A_vb6_unit_is_named_by_its_vb_name_in_the_shared_namespace()
    {
        var unit = Vb6Unit("Class/ConflictMatrix.cls", ("Run", 20, 30));
        var src = "VERSION 1.0 CLASS\nBEGIN\nEND\nAttribute VB_Name = \"clsConflictMatrix\"\nOption Explicit\n";
        var named = FaithfulConversion.ForLanguage(unit, "vb6", src);
        Assert.Equal("clsConflictMatrix", named.ClassName);
        Assert.Equal(FaithfulConversion.Vb6Namespace, named.Namespace);
        Assert.Same(unit, FaithfulConversion.ForLanguage(unit, "delphi", src));
        Assert.Equal(unit.ClassName, FaithfulConversion.ForLanguage(unit, "vb6", "no attribute here").ClassName);
    }

    [Fact]
    public void A_small_unit_is_one_part_and_a_large_one_is_split_by_routine_after_its_declarations()
    {
        var small = Vb6Unit("Modules/modA.bas", ("A", 3, 10));
        Assert.Single(FaithfulConversion.PlanParts(small, string.Join('\n', Enumerable.Repeat("x", 20))));

        // 2,500 lines: declarations on lines 1-99, then routines of 600, 700, 500 and 600 lines.
        var unit = Vb6Unit("Modules/modBig.bas", ("A", 100, 699), ("B", 700, 1399), ("C", 1400, 1899), ("D", 1900, 2499));
        unit = unit with { ClassName = "modBig" };
        var text = string.Join('\n', Enumerable.Range(1, 2500).Select(i => $"' line {i}"));
        var parts = FaithfulConversion.PlanParts(unit, text, maxLines: 1200);

        Assert.Equal(4, parts.Count);   // declarations, A, B+C, D
        Assert.All(parts, p => Assert.Equal(4, p.Count));
        Assert.Empty(parts[0].RoutineNames);
        Assert.Equal("src/modBig.cs", parts[0].MainFile);
        Assert.Contains("PART 1 of 4", parts[0].Note);
        Assert.Contains("public static partial class modBig", parts[0].Note);
        Assert.Equal(99, parts[0].SourceText.Split('\n').Length);
        Assert.Equal(["A"], parts[1].RoutineNames);
        Assert.Equal(["B", "C"], parts[2].RoutineNames);
        Assert.Equal(["D"], parts[3].RoutineNames);
        Assert.Equal("src/modBig.Part3.cs", parts[2].MainFile);
        Assert.Contains("' line 700", parts[2].SourceText);
        Assert.Contains("' line 1899", parts[2].SourceText);
        Assert.DoesNotContain("' line 1900", parts[2].SourceText);
        Assert.Contains("' line 50", parts[2].SourceText);   // every part sees the declarations
        Assert.Contains("\"part\":3", parts[2].RoutinesJson);

        // A form's first part also writes its page.
        var form = Vb6Unit("Form/frmBig.frm", ("Form_Load", 1500, 2600)) with { ClassName = "frmBig" };
        var formParts = FaithfulConversion.PlanParts(form, string.Join('\n', Enumerable.Range(1, 2700).Select(i => "x")), 1200);
        Assert.Contains("src/View_frmBig.razor", formParts[0].Note);
        Assert.Contains("public partial class frmBig : VbForm", formParts[1].Note);
        Assert.True(FaithfulConversion.SupportsParts("vb6"));
        Assert.False(FaithfulConversion.SupportsParts("delphi"));
    }

    [Fact]
    public void Later_parts_may_add_files_but_never_replace_one()
    {
        var p1 = new List<FaithfulConversion.PackageFile> { new("src/modBig.cs", "csharp", "decl", ["INV-1"]), new("src/Stubs/Units/Globals.cs", "csharp", "g1", []) };
        var p2 = new List<FaithfulConversion.PackageFile> { new("src/modBig.Part2.cs", "csharp", "A", ["SE-1"]), new("src/modBig.cs", "csharp", "redeclared", ["INV-2"]) };
        var merged = FaithfulConversion.MergeParts([p1, p2]);
        Assert.Equal(["src/modBig.cs", "src/Stubs/Units/Globals.cs", "src/modBig.Part2.cs"], merged.Select(f => f.Path));
        Assert.Equal("decl", merged[0].Content);
        Assert.Equal(["INV-1", "INV-2"], merged[0].DerivedFromClaimIds);
        var section = FaithfulConversion.EarlierPartsSection(merged, "src/modBig.cs");
        Assert.Contains("src/Stubs/Units/Globals.cs", section);
        Assert.DoesNotContain("src/modBig.Part2.cs", section);
    }

    [Fact]
    public void The_unit_key_changes_with_the_source_the_signed_specs_or_the_target()
    {
        var unit = Vb6Unit("Modules/modA.bas", ("A", 3, 10));
        var signed = unit with { SignedSpecs = [new FaithfulConversion.UnitSpec("A", "{\"invariants\":[{\"id\":\"INV-1\"}]}")] };
        var k = FaithfulConversion.UnitKey("src", unit, "dotnet10-faithful");
        Assert.Equal(k, FaithfulConversion.UnitKey("src", unit, "dotnet10-faithful"));
        Assert.Equal(k, FaithfulConversion.UnitKey("src".Replace("\n", "\r\n"), unit, "dotnet10-faithful"));
        Assert.NotEqual(k, FaithfulConversion.UnitKey("src2", unit, "dotnet10-faithful"));
        Assert.NotEqual(k, FaithfulConversion.UnitKey("src", signed, "dotnet10-faithful"));
        Assert.NotEqual(k, FaithfulConversion.UnitKey("src", unit, "dotnet10"));
    }

    [Fact]
    public void The_vb6_archetype_ships_its_runtime_and_keeps_its_exemplars_out_of_packages()
    {
        var dir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes", "dotnet10-faithful", "faithful-vb6-unit"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "archetype.json")));
        var root = manifest.RootElement;
        Assert.Equal(["vb6"], root.GetProperty("compatibleSchemas").EnumerateArray().Select(e => e.GetString()));
        var files = root.GetProperty("files").EnumerateArray()
            .Select(f => (Path: f.GetProperty("path").GetString()!, Language: f.GetProperty("language").GetString()!, Content: File.ReadAllText(Path.Combine(dir, f.GetProperty("path").GetString()!))))
            .ToList();
        Assert.Contains(files, f => f.Path == "src/Vb6Runtime/Controls.cs");
        Assert.Contains(files, f => f.Path == "src/Vb6Runtime/Interaction.cs");
        Assert.True(FaithfulConversion.IsExemplar("src/Exemplar/frmRates.cs"));
        Assert.True(FaithfulConversion.IsExemplar("src/Exemplar/View_frmRates.razor"));
        Assert.False(FaithfulConversion.IsExemplar("src/Vb6Runtime/Controls.cs"));

        var shown = FaithfulConversion.ExemplarSourceOf(files);
        Assert.Contains("### src/Exemplar/modRates.cs", shown);
        Assert.Contains("```razor", shown);
        Assert.DoesNotContain("namespace Faithful.Vb6;", shown);   // the runtime is not an exemplar
        Assert.Contains("@inherits VbFormView<frmRates>", shown);
    }

    /// <summary>Walks up from the test assembly to the api folder that holds src/ and tests/.</summary>
    private static string FindInSourceTree(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}");
    }
}
