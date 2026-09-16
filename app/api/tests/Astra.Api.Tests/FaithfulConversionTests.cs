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
    public void The_faithful_archetype_lists_every_language_with_a_faithful_prompt()
    {
        var archetypeDir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes", "dotnet10-faithful", "faithful-delphi-unit"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(archetypeDir, "archetype.json")));
        var compatible = manifest.RootElement.GetProperty("compatibleSchemas").EnumerateArray().Select(e => e.GetString()).ToHashSet();

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
