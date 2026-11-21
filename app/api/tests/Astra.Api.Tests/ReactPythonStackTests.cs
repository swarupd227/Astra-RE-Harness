using Astra.Api.Llm;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Llm.Prompts;
using Astra.Api.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The react-python target: its archetype loads, its scaffold prompt resolves, and the Python half
/// of its gates reads pytest's summary. The archetype's own build and tests are run for real by the
/// gates; these checks keep the wiring from silently falling back to the .NET path.
/// </summary>
public class ReactPythonStackTests
{
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

    private static IConfiguration Config(string key, string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Astra.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void The_archetype_loads_for_a_vba_routine_with_both_halves_and_their_tests()
    {
        var dir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes"));
        var registry = new ArchetypeRegistry(new Env(), Config("Llm:ArchetypeDir", dir), NullLogger<ArchetypeRegistry>.Instance);

        var archetype = registry.PickForSubroutine(CompileValidator.ReactPythonStack, "CalcPremium", "vb6");
        Assert.NotNull(archetype);
        Assert.StartsWith("production", archetype!.Manifest.Status);
        var paths = archetype.Files.Select(f => f.Path).ToList();
        Assert.Contains("backend/requirements.txt", paths);
        Assert.Contains("frontend/package.json", paths);
        Assert.Contains(paths, p => p.StartsWith("backend/tests/test_", StringComparison.Ordinal) && p.EndsWith(".py"));
        Assert.Contains(paths, p => p.StartsWith("frontend/src/", StringComparison.Ordinal) && p.EndsWith(".test.tsx"));

        // Nothing built or installed ever ships inside the template.
        Assert.DoesNotContain(paths, p => p.Contains("node_modules") || p.Contains("__pycache__") || p.Contains(".venv"));
        var onDisk = Directory.EnumerateFileSystemEntries(archetype.ArchetypeDir, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(onDisk, p => p.Contains("node_modules") || p.Contains("__pycache__") || p.Contains(".venv"));

        // The front end runs jest, whose "Tests:" summary the test-pack gate parses.
        var package = archetype.Files.Single(f => f.Path == "frontend/package.json").Content;
        Assert.Contains("\"test\": \"jest", package);
    }

    [Fact]
    public void The_scaffold_prompt_resolves_for_the_stack()
    {
        Assert.Equal("react-python", AnthropicScaffoldProvider.ScaffoldPromptFamily("react-python"));
        var dir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Prompts"));
        var prompts = new PromptLibrary(new Env(), Config("Llm:PromptDir", dir), NullLogger<PromptLibrary>.Instance);
        var prompt = prompts.GetLatest("common", "react-python", "scaffold-generate");
        Assert.NotNull(prompt);
    }

    [Theory]
    [InlineData("tests/test_x.py ...\n7 passed, 1 warning in 3.98s\n", 7, 0, 0)]
    [InlineData("=========== 5 passed, 2 failed, 1 skipped in 0.31s ===========\n", 5, 2, 1)]
    [InlineData("== 1 failed, 3 passed, 1 error in 1.20s ==\n", 3, 2, 0)]
    public void Pytest_summaries_are_counted(string log, int passed, int failed, int skipped)
    {
        var s = PythonRunner.ParseSummary(log);
        Assert.NotNull(s);
        Assert.Equal((passed, failed, skipped, passed + failed + skipped), s!.Value);
    }

    [Fact]
    public void No_summary_is_null_not_zero()
    {
        Assert.Null(PythonRunner.ParseSummary("ModuleNotFoundError: No module named 'fastapi'\n=== exit 4 ===\n"));
    }

    [Theory]
    [InlineData("CalcPremium", "calc_premium")]
    [InlineData("INV-1", "inv_1")]
    [InlineData("2nd pass", "_2nd_pass")]
    public void Python_names_are_snake_case(string input, string expected) =>
        Assert.Equal(expected, TestPackGenerator.SnakeCase(input));
}
