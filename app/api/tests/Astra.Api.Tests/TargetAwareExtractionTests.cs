using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Artefacts;
using Astra.Api.Evidence;
using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Astra.Api.Llm.Schemas;
using Astra.Api.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// PHP track P3. A spec is written for the stack its routine is going to (the project's target, else the language's
/// default), PHP specs carry the web surface a REST migration must keep (HTTP contracts, access rules, data access),
/// and extraction is told which routes a routine serves from the project's route table.
/// </summary>
public class TargetAwareExtractionTests
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

    private static readonly Lazy<PromptLibrary> Prompts = new(() => new PromptLibrary(new Env(),
        Config("Llm:PromptDir", FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Prompts"))),
        NullLogger<PromptLibrary>.Instance));

    private static readonly Lazy<SpecSchemaProvider> Schemas = new(() => new SpecSchemaProvider(new Env(),
        Config("Llm:SchemaDir", FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "Schemas"))),
        NullLogger<SpecSchemaProvider>.Instance));

    private static string ExtractTarget(string? projectTarget, string language) =>
        TargetStacks.Resolve(projectTarget, language, t => Prompts.Value.GetLatest(language, t, "extract") is not null);

    // ── which target a spec is written for ──────────────────────────────

    [Theory]
    [InlineData("php", "java-spring")]
    [InlineData("cobol", "java-spring")]
    [InlineData("unibasic", "java-spring")]
    [InlineData("openedge", "java-spring")]
    [InlineData("fortran-f77", "dotnet10")]
    [InlineData("delphi", "dotnet10")]
    [InlineData("vb6", "dotnet10-blazor")]
    [InlineData("csharp", "dotnet10-webapi")]
    public void A_project_without_a_target_extracts_for_its_languages_default(string language, string expected) =>
        Assert.Equal(expected, ExtractTarget(null, language));

    [Fact]
    public void A_project_that_names_its_target_extracts_for_it()
    {
        Assert.Equal("dotnet10", ExtractTarget("dotnet10", "php"));
        Assert.Equal("dotnet8", ExtractTarget("dotnet8", "php"));
        Assert.Equal("java-spring", ExtractTarget("java-spring", "delphi"));
    }

    [Fact]
    public void A_target_with_no_prompt_for_the_language_falls_back_to_its_default_then_dotnet10()
    {
        // react-python has no PHP extract prompt: the language default (java-spring) is used.
        Assert.Equal("java-spring", ExtractTarget("react-python", "php"));
        // An unknown language has no default: .NET 10.
        Assert.Equal(TargetStacks.Fallback, TargetStacks.Resolve(null, "nope", _ => false));
        Assert.Equal(new[] { "java-spring", "dotnet10" }, TargetStacks.Candidates("not a stack!", "php"));
    }

    [Fact]
    public void The_assessment_recommends_the_same_default()
    {
        Assert.Equal("java-spring", TargetStacks.DefaultFor("php"));
        Assert.Equal("java-spring", TargetStacks.DefaultFor("openedge"));
        Assert.Null(TargetStacks.DefaultFor("nope"));
    }

    [Theory]
    [InlineData("java-spring", true)]
    [InlineData("dotnet10", true)]
    [InlineData("dotnet10-faithful", true)]
    [InlineData("react-python", true)]
    [InlineData("python", false)]
    [InlineData("Java-Spring", false)]
    [InlineData("dotnet10-", false)]
    public void Target_stack_names_are_checked(string stack, bool known) =>
        Assert.Equal(known, TargetStacks.IsKnown(stack));

    [Fact]
    public void Project_settings_keep_a_target_stack_and_reject_an_unknown_one()
    {
        var (ok, errors) = ProjectSettings.Validate(JsonDocument.Parse("""{"targetStack":" Java-Spring "}""").RootElement);
        Assert.Empty(errors);
        Assert.Equal("java-spring", ok!.TargetStack);
        Assert.Equal("java-spring", ProjectSettings.Parse(ok.ToJson()).TargetStack);

        var (bad, why) = ProjectSettings.Validate(JsonDocument.Parse("""{"targetStack":"cobol"}""").RootElement);
        Assert.Null(bad);
        Assert.Contains(why, e => e.Contains("targetStack"));

        Assert.Null(ProjectSettings.Validate(JsonDocument.Parse("{}").RootElement).Settings!.TargetStack);
    }

    // ── the PHP schema and its prompts ──────────────────────────────────

    [Fact]
    public void The_php_schema_has_the_web_kinds_and_every_kind_is_asked_for()
    {
        var php = Schemas.Value.GetById("php")!;
        var ids = php.ClaimKinds.Select(k => k.Id).ToList();
        Assert.Contains("httpContract", ids);
        Assert.Contains("authorizationRule", ids);
        Assert.Contains("dataAccess", ids);
        Assert.Equal(ids.OrderBy(x => x), php.PromptHints!.PreferredClaimTypes!.OrderBy(x => x));
        Assert.Contains("java-spring", php.CompatibleTargetStacks);
        Assert.Contains("dotnet10", php.CompatibleTargetStacks);
    }

    [Theory]
    [InlineData("java-spring")]
    [InlineData("dotnet10")]
    public void Each_php_extract_prompt_asks_for_every_claim_section_of_the_schema(string target)
    {
        var prompt = Prompts.Value.GetLatest("php", target, "extract")!;
        Assert.Equal("v2.0", prompt.Version);
        var text = prompt.SystemTemplate;
        foreach (var kind in Schemas.Value.GetById("php")!.ClaimKinds)
            Assert.True(text.Contains($"\"{kind.SpecJsonField}\""), $"{target} prompt does not ask for {kind.SpecJsonField}");
        Assert.Contains("{{neighbourhood}}", prompt.UserTemplate);
    }

    [Fact]
    public void The_web_kinds_are_scored_and_surveyed()
    {
        var spec = JsonDocument.Parse("""
            {"http_contracts":[{"id":"HTTP-1","method":"GET","path":"/x","description":"answers 404 when missing"}],
             "authorization_rules":[{"id":"AUTH-1","rule":"logged in only"}],
             "data_accesses":[{"id":"DA-1","description":"reads customers","parameterised":"no"}]}
            """).RootElement;
        var buckets = ClaimKindBucketer.Bucket(spec);
        Assert.Contains(buckets["httpContract"], s => s.Contains("404"));
        Assert.Contains(buckets["authorizationRule"], s => s.Contains("logged in"));
        Assert.Contains(buckets["dataAccess"], s => s.Contains("customers"));
        foreach (var k in new[] { "httpContract", "authorizationRule", "dataAccess" })
            Assert.Contains(ClaimKindBucketer.KnownKinds, x => x.Key == k);
    }

    [Fact]
    public void Php_golden_cases_name_real_kinds_with_patterns_that_compile()
    {
        var dir = FindInSourceTree(Path.Combine("src", "Astra.Api", "Llm", "GoldenDataset", "php"));
        var kinds = Schemas.Value.GetById("php")!.ClaimKinds.Select(k => k.Id).ToHashSet();
        var files = Directory.GetFiles(dir, "*.json");
        Assert.True(files.Length >= 9);
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            foreach (var c in root.GetProperty("expectedClaims").EnumerateArray())
            {
                Assert.Contains(c.GetProperty("kind").GetString()!, kinds);
                _ = new Regex(c.GetProperty("pattern").GetString()!);   // compiles
            }
        }
    }

    // ── which routes a routine serves ───────────────────────────────────

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly IReadOnlyList<RouteTrigger> Triggers = new[]
    {
        new RouteTrigger("GET /invoices/{id}",
            J("""{"method":"GET","path":"/invoices/{id}","handler":"InvoiceController::show","detector":"router-call","file":"routes/api.php"}"""),
            EntityVocabulary.Class, "InvoiceController", J("""{"method":"show"}"""), new[] { "PHP-00000001" }),
        new RouteTrigger("POST /invoices",
            J("""{"method":"POST","path":"/invoices","detector":"router-call","file":"routes/api.php"}"""),
            EntityVocabulary.Class, "InvoiceController", J("""{"method":"store"}"""), new[] { "PHP-00000002" }),
        new RouteTrigger("GET /ping",
            J("""{"method":"GET","path":"/ping","detector":"router-call"}"""),
            EntityVocabulary.Class, "PingController", null, Array.Empty<string>()),
        new RouteTrigger("ANY /legacy/api.php?action=getCustomer",
            J("""{"method":"ANY","path":"/legacy/api.php","parameter":"action","value":"getCustomer","detector":"front-controller","file":"legacy/api.php","calls":["get_customer"]}"""),
            EntityVocabulary.Script, "legacy/api.php", J("""{"calls":["get_customer"]}"""), new[] { "PHP-00000003" }),
    };

    [Fact]
    public void A_controller_method_serves_the_routes_whose_link_names_it()
    {
        var served = ServedRoutes.For("show", "app/Http/Controllers/InvoiceController.php", new[] { "InvoiceController" }, Triggers);
        var r = Assert.Single(served);
        Assert.Equal(("GET /invoices/{id}", "GET", "/invoices/{id}", ServedRoutes.Handler), (r.Name, r.Method, r.Path, r.Role));
        Assert.Equal("routes/api.php", r.DeclaredIn);
        Assert.Equal(new[] { "PHP-00000001" }, r.EvidenceIds);

        Assert.Equal("POST /invoices", Assert.Single(ServedRoutes.For("store", "x.php", new[] { "App\\Http\\Controllers\\InvoiceController" }, Triggers)).Name);
        Assert.Empty(ServedRoutes.For("show", "x.php", new[] { "OtherController" }, Triggers));
        // An invokable controller (no method on the link) is served by __invoke.
        Assert.Equal("GET /ping", Assert.Single(ServedRoutes.For("__invoke", "x.php", new[] { "PingController" }, Triggers)).Name);
    }

    [Fact]
    public void A_front_controller_dispatches_and_the_function_its_case_calls_handles()
    {
        var script = Assert.Single(ServedRoutes.For("api", "legacy/api.php", Array.Empty<string>(), Triggers));
        Assert.Equal(ServedRoutes.Dispatcher, script.Role);
        Assert.Equal(("action", "getCustomer"), (script.Parameter, script.Value));

        var handler = Assert.Single(ServedRoutes.For("get_customer", "legacy/api.php", Array.Empty<string>(), Triggers));
        Assert.Equal(ServedRoutes.Handler, handler.Role);
        Assert.Empty(ServedRoutes.For("other", "legacy/api.php", Array.Empty<string>(), Triggers));
    }

    [Fact]
    public void The_routes_are_shown_to_the_model_beside_the_neighbourhood()
    {
        var served = ServedRoutes.For("show", "x.php", new[] { "InvoiceController" }, Triggers);
        var nbh = Neighbourhood.Empty with { Routes = served };
        Assert.False(nbh.IsEmpty);
        var text = AnthropicPromptTemplate.RenderNeighbourhood(nbh);
        Assert.Contains("### Routes this routine serves", text);
        Assert.Contains("`GET /invoices/{id}` — handler (declared in routes/api.php; router-call)", text);
        Assert.Equal("", AnthropicPromptTemplate.RenderRoutes(Array.Empty<ServedRoute>()));
    }
}
