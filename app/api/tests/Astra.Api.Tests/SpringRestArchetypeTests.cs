using System.Text.Json;
using System.Xml.Linq;
using Astra.Api.Copilot;
using Astra.Api.Endpoints;
using Astra.Api.Llm;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Llm.Prompts;
using Astra.Api.Work;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// PHP track P4. A web routine is generated into a real Spring Boot REST service (controller, service, JdbcTemplate
/// repository, contract tests); the target follows the project and the language; the scaffold prompt cites every
/// claim section and carries the language's mapping note; the target can be set in a sentence.
/// </summary>
public class SpringRestArchetypeTests
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

    private static readonly Lazy<ArchetypeRegistry> Archetypes = new(() => new ArchetypeRegistry(new Env(),
        Config("Llm:ArchetypeDir", FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Archetypes"))),
        NullLogger<ArchetypeRegistry>.Instance));

    private static readonly Lazy<PromptLibrary> Prompts = new(() => new PromptLibrary(new Env(),
        Config("Llm:PromptDir", FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Prompts"))),
        NullLogger<PromptLibrary>.Instance));

    // ── the archetype ───────────────────────────────────────────────────

    [Fact]
    public void A_php_web_routine_gets_the_production_rest_archetype()
    {
        var picked = Archetypes.Value.PickForSubroutine("java-spring", "show", "php")!;
        Assert.Equal("spring-rest-service", picked.Manifest.Id);
        Assert.StartsWith("production", picked.Manifest.Status);
        // A name the narrower preview archetype lists still reaches it.
        Assert.Equal("canonical-php-catalog-service", Archetypes.Value.PickForSubroutine("java-spring", "applyCoupon", "php")!.Manifest.Id);
    }

    [Fact]
    public void The_archetype_is_a_real_spring_service_with_contract_tests()
    {
        var a = Archetypes.Value.Get("java-spring", "spring-rest-service")!;
        var paths = a.Files.Select(f => f.Path).ToList();
        Assert.Contains("src/main/resources/schema.sql", paths);
        string Content(string suffix) => a.Files.Single(f => f.Path.EndsWith(suffix, StringComparison.Ordinal)).Content;
        Assert.Contains("@RestController", Content("InvoiceController.java"));
        Assert.Contains("@RestControllerAdvice", Content("ApiExceptionHandler.java"));
        Assert.Contains("HttpStatus.UNPROCESSABLE_ENTITY", Content("ApiExceptionHandler.java"));
        Assert.Contains("JdbcTemplate", Content("InvoiceRepository.java"));
        Assert.DoesNotContain("\" +", Content("InvoiceRepository.java"));   // no SQL built by concatenation
        Assert.Contains("@WebMvcTest", Content("InvoiceControllerTests.java"));
        Assert.Contains("@JdbcTest", Content("InvoiceRepositoryTests.java"));
    }

    [Fact]
    public void The_archetype_needs_nothing_the_maven_sidecar_does_not_cache_offline()
    {
        static HashSet<string> Deps(string pom)
        {
            var doc = XDocument.Parse(pom);
            XNamespace ns = "http://maven.apache.org/POM/4.0.0";
            return doc.Root!.Element(ns + "dependencies")!.Elements(ns + "dependency")
                .Select(d => $"{d.Element(ns + "groupId")!.Value}:{d.Element(ns + "artifactId")!.Value}").ToHashSet();
        }
        var archetypePom = Archetypes.Value.Get("java-spring", "spring-rest-service")!.Files.Single(f => f.Path == "pom.xml").Content;
        var warmupPom = File.ReadAllText(FindInSourceTree(Path.Combine("maven-sidecar", "warmup-spring", "pom.xml")));
        Assert.Subset(Deps(warmupPom), Deps(archetypePom));
    }

    // ── which target ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "php", "java-spring")]
    [InlineData("dotnet8", "php", "dotnet8")]
    [InlineData("react-python", "php", "java-spring")]   // no react-python archetype for php: the language default
    [InlineData(null, "delphi", "dotnet10")]
    [InlineData(null, "nope", "dotnet10")]
    public void The_default_target_follows_the_project_then_the_language(string? project, string language, string expected) =>
        Assert.Equal(expected, ScaffoldEndpoints.PreferredStack(new[] { "dotnet10", "dotnet8", "java-spring" }, project, language));

    [Fact]
    public void A_php_routine_resolves_to_spring_and_is_not_gated()
    {
        var target = ScaffoldTargetResolver.Resolve(Archetypes.Value, "php", "show", requested: null);
        Assert.True(target.Ok, target.Error);
        Assert.Equal(("java-spring", "spring-rest-service"), (target.TargetStack, target.ArchetypeId));
        var dotnet = ScaffoldTargetResolver.Resolve(Archetypes.Value, "php", "show", requested: null, projectTarget: "dotnet8");
        Assert.Equal("dotnet8", dotnet.TargetStack);
    }

    // ── the scaffold prompt ─────────────────────────────────────────────

    [Fact]
    public void A_specs_own_claim_sections_are_valid_citations()
    {
        var ids = AnthropicScaffoldProvider.ExtractValidClaimIds("""
            {"invariants":[{"id":"INV-1"}],"http_contracts":[{"id":"HTTP-1","request":[{"name":"id"}]}],
             "data_accesses":[{"id":"DA-1"}],"summary":"x","inputs":[{"id":"in.id"}]}
            """);
        Assert.Equal(new[] { "DA-1", "HTTP-1", "INV-1", "in.id" }, ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void The_java_scaffold_prompt_cites_any_section_and_follows_the_mapping_note()
    {
        var prompt = Prompts.Value.GetLatest("common", "java-spring", "scaffold-generate")!;
        Assert.Equal("v5.0", prompt.Version);
        Assert.Contains("ANY of its claim arrays", prompt.SystemTemplate);
        Assert.Contains("mapping note", prompt.SystemTemplate);

        var note = AnthropicScaffoldProvider.ScaffoldMappingNote(Prompts.Value, "php", "java-spring");
        Assert.NotNull(note);
        Assert.Contains("http_contracts", note);
        Assert.Contains("bound", note);
        Assert.Null(AnthropicScaffoldProvider.ScaffoldMappingNote(Prompts.Value, "fortran-f77", "java-spring"));
    }

    // ── the target as a sentence ────────────────────────────────────────

    [Theory]
    [InlineData("Migrate this project to Java Spring", "java-spring")]
    [InlineData("migrate the programme to .NET 8", "dotnet8")]
    [InlineData("Target .NET 10", "dotnet10")]
    [InlineData("set the migration target to React + Python", "react-python")]
    [InlineData("use the default target", "default")]
    public void Setting_the_target_is_a_sentence(string text, string stack)
    {
        var routed = MockCopilotBrain.RouteOrNull(text);
        Assert.NotNull(routed);
        Assert.Equal("set_target_stack", routed!.Value.Tool);
        var input = JsonSerializer.SerializeToElement(routed.Value.Input);
        Assert.Equal(stack, input.GetProperty("targetStack").GetString());
    }

    [Theory]
    [InlineData("default", null)]
    [InlineData("Java Spring", "java-spring")]
    [InlineData(".NET 8", "dotnet8")]
    [InlineData("dotnet10", "dotnet10")]
    [InlineData("angular-java", "angular-java")]
    public void Target_names_are_normalised(string text, string? expected) =>
        Assert.Equal(expected, CopilotToolRegistry.NormaliseTarget(text));

    [Fact]
    public void Generating_code_is_still_routed_as_before()
    {
        Assert.Equal("generate_scaffold", MockCopilotBrain.RouteOrNull("Convert `show` to Java")!.Value.Tool);
        Assert.Equal("list_programmes", MockCopilotBrain.RouteOrNull("list the projects")!.Value.Tool);
    }
}
