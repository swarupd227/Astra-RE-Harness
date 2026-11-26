using Astra.Api.Llm;
using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Q5: the generator is told the standards the QUALITY gate checks, and the references it copies meet them.</summary>
public class CodingStandardsTests
{
    private static readonly string LlmDir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm"));
    private static string? ReadAsset(string file)
    {
        var p = Path.Combine(LlmDir, "Prompts", "common", file);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    [Theory]
    [InlineData("java-spring", new[] { "java" })]
    [InlineData("angular-java", new[] { "java", "typescript" })]
    [InlineData("react-python", new[] { "python", "typescript" })]
    [InlineData("angular-dotnet8", new[] { "csharp", "typescript" })]
    [InlineData("dotnet10-webapi", new[] { "csharp" })]
    [InlineData("dotnet10-faithful", new[] { "csharp" })]
    [InlineData("dotnet8", new[] { "csharp" })]
    public void Every_target_stack_gets_the_standards_of_the_languages_it_is_written_in(string stack, string[] languages)
    {
        Assert.Equal(languages, CodingStandards.LanguagesFor(stack));
        var section = CodingStandards.SectionFor(stack, ReadAsset);
        Assert.StartsWith(CodingStandards.Heading, section);
        foreach (var l in languages)
            Assert.Contains(ReadAsset(CodingStandards.FileName(l))!.Trim(), section);
    }

    [Fact]
    public void A_stack_without_standards_keeps_its_prompt_unchanged()
    {
        Assert.Equal("", CodingStandards.SectionFor("cobol-batch", ReadAsset));
        Assert.Equal("system", CodingStandards.AppendTo("system", ""));
        Assert.EndsWith("## Coding and security standards\n", CodingStandards.AppendTo("system\n", "## Coding and security standards"));
    }

    [Theory]
    [InlineData("java", "@RequestBody")]
    [InlineData("java", "parameterised")]
    [InlineData("python", "shell=True")]
    [InlineData("typescript", "dangerouslySetInnerHTML")]
    [InlineData("csharp", "FromSqlRaw")]
    public void The_standards_name_what_the_gate_checks(string language, string phrase) =>
        Assert.Contains(phrase, ReadAsset(CodingStandards.FileName(language)));

    /// <summary>The model copies a reference archetype's shape, so a reference that breaks a blocking rule teaches
    /// the breach to every package made from it.</summary>
    [Fact]
    public void Every_reference_archetype_passes_the_blocking_rules()
    {
        var root = Path.Combine(LlmDir, "Archetypes");
        var failures = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories))
        {
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains("node_modules"))
                .Select(f => (Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f)))
                .ToList();
            foreach (var f in QualityRules.Scan(files).Where(f => QualityRules.BlockingByDefault.Contains(f.Severity)))
                failures.Add($"{Path.GetRelativePath(root, dir)}: {QualityRules.FormatLine(f)}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
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
        throw new DirectoryNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}");
    }
}
