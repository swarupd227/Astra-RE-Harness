using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The parser sidecar states facts (PHP today) as FactSet JSON; the API stores them as it stores the facts it reads
/// itself. These tests pin the contract between the two: the JSON the sidecar's `php_facts.py` writes reads back as
/// the same entities and links, and a route the application serves is counted apart from an endpoint it calls.
/// </summary>
public class SidecarFactsContractTests
{
    // Exactly the shape php_facts.Facts.to_json() writes (camelCase keys, optional locator/attrs, confidence).
    private const string SidecarJson = """
        {"entities":[
          {"type":"route","name":"GET /invoices/{id}","locator":"L4-4","attrs":{"method":"GET","path":"/invoices/{id}","handler":"InvoiceController::show","detector":"router-call","file":"routes/api.php"}},
          {"type":"class","name":"InvoiceController"},
          {"type":"script","name":"legacy/api.php","locator":"L2-2","attrs":{"language":"php"}},
          {"type":"table","name":"customers"}
        ],
        "links":[
          {"relation":"triggers","from":{"type":"route","name":"GET /invoices/{id}"},"to":{"type":"class","name":"InvoiceController"},"confidence":"high","locator":"L4-4","attrs":{"method":"show"}},
          {"relation":"reads","from":{"type":"script","name":"legacy/api.php"},"to":{"type":"table","name":"customers"},"confidence":"medium","locator":"L5-5"}
        ]}
        """;

    [Fact]
    public void TheSidecarsFacts_ReadBackAsEntitiesAndLinks()
    {
        var facts = FactSet.FromJson(SidecarJson);

        Assert.Equal(4, facts.Entities.Count);
        var route = facts.Entities.Single(e => e.Type == EntityVocabulary.Route);
        Assert.Equal("GET /invoices/{id}", route.Name);
        Assert.Equal("L4-4", route.Locator);
        Assert.Equal("InvoiceController::show", route.Attrs!["handler"]?.ToString());

        var triggers = facts.Links.Single(l => l.Relation == EntityVocabulary.Triggers);
        Assert.Equal(new EntityRefFact(EntityVocabulary.Route, "GET /invoices/{id}"), triggers.From);
        Assert.Equal(new EntityRefFact(EntityVocabulary.Class, "InvoiceController"), triggers.To);
        Assert.Equal(EntityVocabulary.High, triggers.Confidence);

        var reads = facts.Links.Single(l => l.Relation == EntityVocabulary.Reads);
        Assert.Equal(EntityVocabulary.Medium, reads.Confidence);
        Assert.Equal("L5-5", reads.Locator);
    }

    [Fact]
    public void EmptyOrBrokenFacts_AreNoFacts()
    {
        Assert.True(FactSet.FromJson("").IsEmpty);
        Assert.True(FactSet.FromJson("{not json").IsEmpty);
        Assert.True(FactSet.FromJson("""{"entities":[],"links":[]}""").IsEmpty);
    }

    [Fact]
    public void RoutesServed_AreCountedApartFromEndpointsCalled()
    {
        var entities = new List<InventoryEntity>
        {
            new("route:GET /a", EntityVocabulary.Route, "GET /a", true, new[] { "PHP-00000001" }),
            new("route:POST /a", EntityVocabulary.Route, "POST /a", true, new[] { "PHP-00000002" }),
            new("endpoint:GET {Api}/x", EntityVocabulary.Endpoint, "GET {Api}/x", true, new[] { "CS-00000003" }),
        };
        var measures = InventoryRules.Measures(entities, Array.Empty<InventoryLink>(), routines: 5, new Dictionary<string, int> { ["routes"] = 3 });

        var routes = measures.Single(m => m.Key == "routes");
        Assert.Equal(2, routes.Found);
        Assert.Equal(3, routes.Expected);
        Assert.Equal("HTTP routes served", routes.Label);
        Assert.Equal(1, measures.Single(m => m.Key == "endpoints").Found);
        Assert.Equal("HTTP route served", EntityVocabulary.Types[EntityVocabulary.Route]);
    }

    [Theory]
    [InlineData("app/Http/Controllers/InvoiceController.php", true)]
    [InlineData("legacy/API.PHP", true)]
    [InlineData("src/Program.cs", false)]
    [InlineData("views/home.phtml", false)]
    public void WhichFilesTheSidecarReadsFactsFrom(string path, bool expected) =>
        Assert.Equal(expected, EvidenceService.HasSidecarFacts(path));
}
