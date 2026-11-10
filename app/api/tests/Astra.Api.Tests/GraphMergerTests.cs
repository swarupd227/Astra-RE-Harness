using System.Text.Json;
using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

public class GraphMergerTests
{
    private static FileFactSet File(string path, FactSet facts, params (string Locator, string Id)[] ids) =>
        new(path, facts, ids.ToDictionary(i => i.Locator, i => i.Id));

    private static FactSet Facts(IEnumerable<EntityFact>? entities = null, IEnumerable<LinkFact>? links = null) =>
        new((entities ?? Array.Empty<EntityFact>()).ToList(), (links ?? Array.Empty<LinkFact>()).ToList());

    private static EntityFact E(string type, string name, string? locator = null, params (string Key, object? Value)[] attrs) =>
        new(type, name, locator, attrs.Length == 0 ? null : Attrs.Of(attrs));

    private static LinkFact L(string relation, (string Type, string Name) from, (string Type, string Name) to, string? locator = null, string confidence = "high", params (string Key, object? Value)[] attrs) =>
        new(relation, new EntityRefFact(from.Type, from.Name), new EntityRefFact(to.Type, to.Name), locator, confidence, attrs.Length == 0 ? null : Attrs.Of(attrs));

    private static MergedEntity Get(MergedGraph g, string type, string name) =>
        Assert.Single(g.Entities, e => e.Type == type && e.Key == GraphMerger.KeyOf(name));

    [Fact]
    public void AJobThatRunsAProcedure_AndTheScriptThatDefinesIt_MeetOnTheSameEntity()
    {
        var sql = File("db/procs/usp_PostStage.sql",
            Facts(new[] { E("procedure", "usp_PostStage", "L1-20") }), ("L1-20", "SQL-aaaaaaaa"));
        var job = File("batch/uc4/TLR_EOD_POST.xml",
            Facts(new[] { E("job", "TLR_EOD_POST", "xpath:/JOBS") }, new[] { L("runs", ("job", "TLR_EOD_POST"), ("procedure", "usp_PostStage"), "xpath:/JOBS") }),
            ("xpath:/JOBS", "JOB-bbbbbbbb"));

        var g = GraphMerger.Merge(new[] { job, sql });

        var proc = Get(g, "procedure", "usp_PostStage");
        Assert.True(proc.Declared);
        Assert.Equal(new[] { "SQL-aaaaaaaa" }, proc.EvidenceIds);
        var link = Assert.Single(g.Links);
        Assert.Same(proc, link.To);
        Assert.Equal(new[] { "JOB-bbbbbbbb" }, link.EvidenceIds);
    }

    [Fact]
    public void AThingOnlyALinkNames_IsKept_ButNotDeclared_AndCitesWhereItWasMentioned()
    {
        var job = File("batch/uc4/TLR_EOD_POST.xml",
            Facts(new[] { E("job", "TLR_EOD_POST", "xpath:/JOBS") }, new[] { L("runs", ("job", "TLR_EOD_POST"), ("procedure", "usp_PostStage"), "xpath:/JOBS") }),
            ("xpath:/JOBS", "JOB-bbbbbbbb"));

        var g = GraphMerger.Merge(new[] { job });

        var proc = Get(g, "procedure", "usp_PostStage");
        Assert.False(proc.Declared);
        Assert.Equal(new[] { "JOB-bbbbbbbb" }, proc.EvidenceIds);
        Assert.Equal(new[] { "batch/uc4/TLR_EOD_POST.xml" }, proc.Paths);
        Assert.True(Get(g, "job", "TLR_EOD_POST").Declared);
    }

    [Fact]
    public void Names_AreMatchedWithoutRegardToCase_AndBrackets_AndTheFirstSpellingIsKept()
    {
        var a = File("a.sql", Facts(new[] { E("table", "TLR_STAGE", "L1-1") }), ("L1-1", "SQL-11111111"));
        var b = File("b.sql", Facts(new[] { E("table", "[tlr_stage]", "L1-1") }), ("L1-1", "SQL-22222222"));
        var g = GraphMerger.Merge(new[] { b, a });
        var table = Assert.Single(g.Entities);
        Assert.Equal("TLR_STAGE", table.Name);
        Assert.Equal(new[] { "SQL-11111111", "SQL-22222222" }, table.EvidenceIds);
    }

    [Fact]
    public void AReferenceToATable_FindsAViewOfThatName()
    {
        var sql = File("db.sql", Facts(
            new[] { E("view", "vw_DrawerLimits", "L1-3"), E("procedure", "usp_GetTellerLimits", "L5-9") },
            new[] { L("reads", ("procedure", "usp_GetTellerLimits"), ("table", "vw_DrawerLimits"), "L5-9") }),
            ("L1-3", "SQL-1"), ("L5-9", "SQL-2"));
        var g = GraphMerger.Merge(new[] { sql });
        Assert.DoesNotContain(g.Entities, e => e.Type == "table");
        Assert.Equal("view", Assert.Single(g.Links).To.Type);
    }

    [Fact]
    public void AReferenceToAProcedure_FindsAFunctionOfThatName()
    {
        var sql = File("db.sql", Facts(
            new[] { E("function", "fn_Limit", "L1-1"), E("procedure", "usp_A", "L3-3") },
            new[] { L("calls", ("procedure", "usp_A"), ("procedure", "fn_Limit"), "L3-3") }),
            ("L1-1", "SQL-1"), ("L3-3", "SQL-2"));
        Assert.Equal("function", Assert.Single(GraphMerger.Merge(new[] { sql }).Links).To.Type);
    }

    [Fact]
    public void ALinkStatedTwice_IsOneLink_WithTheHigherConfidence_AndBothPiecesOfEvidence()
    {
        var a = File("a.xml", Facts(links: new[] { L("reads", ("job", "J"), ("table", "T"), "x1", "medium") }), ("x1", "JOB-1"));
        var b = File("b.xml", Facts(links: new[] { L("reads", ("job", "J"), ("table", "T"), "x2", "high") }), ("x2", "JOB-2"));
        var link = Assert.Single(GraphMerger.Merge(new[] { a, b }).Links);
        Assert.Equal("high", link.Confidence);
        Assert.Equal(new[] { "JOB-1", "JOB-2" }, link.EvidenceIds);
    }

    [Fact]
    public void ALinkFromAThingToItself_IsDropped_ExceptACallBecauseProceduresRecurse()
    {
        var f = File("a.sql", Facts(links: new[]
        {
            L("reads", ("table", "T"), ("table", "T")),
            L("calls", ("procedure", "usp_R"), ("procedure", "usp_R")),
        }));
        Assert.Equal(new[] { "calls" }, GraphMerger.Merge(new[] { f }).Links.Select(l => l.Relation));
    }

    [Fact]
    public void AttributesAreMergedWithoutOverwriting_ListsAreJoined_AndTheEarlierPathWins()
    {
        var a = File("a.sql", Facts(new[]
        {
            E("table", "T", null, ("schema", "dbo"), ("uniqueKeys", new[] { new { name = "UQ_1" } }), ("note", "first")),
        }));
        var b = File("b.sql", Facts(new[]
        {
            E("table", "T", null, ("schema", "other"), ("uniqueKeys", new[] { new { name = "UQ_1" }, new { name = "UQ_2" } }), ("note", "second"), ("extra", 1)),
        }));
        var t = Get(GraphMerger.Merge(new[] { b, a }), "table", "T");
        Assert.Equal("dbo", t.Attrs["schema"].GetString());
        Assert.Equal("first", t.Attrs["note"].GetString());
        Assert.Equal(1, t.Attrs["extra"].GetInt32());
        Assert.Equal(new[] { "UQ_1", "UQ_2" }, t.Attrs["uniqueKeys"].EnumerateArray().Select(k => k.GetProperty("name").GetString()));
    }

    [Fact]
    public void TheResult_DoesNotDependOnTheOrderFilesArriveIn()
    {
        var a = File("a.sql", Facts(new[] { E("table", "A", "L1-1") }, new[] { L("references", ("table", "A"), ("table", "B"), "L1-1") }), ("L1-1", "SQL-1"));
        var b = File("b.sql", Facts(new[] { E("table", "B", "L1-1") }), ("L1-1", "SQL-2"));
        string Shape(MergedGraph g) => string.Join("|", g.Entities.Select(e => $"{e.Type}:{e.Key}:{e.Declared}:{string.Join(",", e.EvidenceIds)}")
            .Concat(g.Links.Select(l => $"{l.Relation}:{l.From.Key}>{l.To.Key}:{l.Confidence}")));
        Assert.Equal(Shape(GraphMerger.Merge(new[] { a, b })), Shape(GraphMerger.Merge(new[] { b, a })));
    }

    [Fact]
    public void AFactWhoseChunkHasNoIdYet_AddsNoEvidence_ButIsStillAFact()
    {
        var f = File("a.sql", Facts(new[] { E("table", "T", "L9-9") }));
        var t = Assert.Single(GraphMerger.Merge(new[] { f }).Entities);
        Assert.Empty(t.EvidenceIds);
        Assert.True(t.Declared);
    }

    [Fact]
    public void ABlankNameIsIgnored()
    {
        var f = File("a.sql", Facts(new[] { E("table", "  ") }, new[] { L("reads", ("job", "J"), ("table", "")) }));
        Assert.Empty(GraphMerger.Merge(new[] { f }).Entities);
    }

    [Fact]
    public void EntitiesAndLinks_AreOrderedByTypeAndName()
    {
        var f = File("a.sql", Facts(
            new[] { E("table", "B"), E("job", "Z"), E("table", "A") },
            new[] { L("writes", ("job", "Z"), ("table", "B")), L("reads", ("job", "Z"), ("table", "A")) }));
        var g = GraphMerger.Merge(new[] { f });
        Assert.Equal(new[] { "job:z", "table:a", "table:b" }, g.Entities.Select(e => $"{e.Type}:{e.Key}"));
        Assert.Equal(new[] { "reads", "writes" }, g.Links.Select(l => l.Relation));
    }
}

public class FactSetTests
{
    [Fact]
    public void FactsSurviveBeingStored_IncludingNestedAttributes()
    {
        var facts = new FactSet(
            new[] { new EntityFact("table", "T", "L1-8", Attrs.Of(("schema", "dbo"), ("uniqueKeys", new[] { new { name = "UQ", columns = new[] { "A" } } }))) },
            new[] { new LinkFact("reads", new("job", "J"), new("table", "T"), "xpath:/JOBS", "medium", Attrs.Of(("tool", "bcp"))) });

        var back = FactSet.FromJson(facts.ToJson());

        var e = Assert.Single(back.Entities);
        Assert.Equal(("table", "T", "L1-8"), (e.Type, e.Name, e.Locator));
        var keys = JsonSerializer.SerializeToElement(e.Attrs!["uniqueKeys"]);
        Assert.Equal("UQ", keys[0].GetProperty("name").GetString());
        Assert.Equal("A", keys[0].GetProperty("columns")[0].GetString());
        var l = Assert.Single(back.Links);
        Assert.Equal(("reads", "J", "T", "medium"), (l.Relation, l.From.Name, l.To.Name, l.Confidence));
        Assert.Equal("bcp", JsonSerializer.SerializeToElement(l.Attrs!["tool"]).GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void StoredFactsThatCannotBeRead_AreNoFacts_NotAnError(string? json) => Assert.True(FactSet.FromJson(json).IsEmpty);

    [Fact]
    public void AttrsOf_LeavesOutNothingButNothing()
    {
        var a = Attrs.Of(("a", null), ("b", ""), ("c", "  "), ("d", new List<string>()), ("e", false), ("f", 0), ("g", "x"));
        Assert.Equal(new[] { "e", "f", "g" }, a.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void TheVocabularyLabelsEveryTypeAndRelationThePlatformProduces()
    {
        foreach (var t in new[] { EntityVocabulary.Table, EntityVocabulary.View, EntityVocabulary.Procedure, EntityVocabulary.Function, EntityVocabulary.Job, EntityVocabulary.Package, EntityVocabulary.File, EntityVocabulary.Script, EntityVocabulary.ConfigKey })
            Assert.True(EntityVocabulary.Types.ContainsKey(t), t);
        foreach (var r in new[] { EntityVocabulary.Reads, EntityVocabulary.Writes, EntityVocabulary.Calls, EntityVocabulary.Runs, EntityVocabulary.Precedes, EntityVocabulary.Contains, EntityVocabulary.Triggers, EntityVocabulary.Configures, EntityVocabulary.References })
            Assert.True(EntityVocabulary.Relations.ContainsKey(r), r);
    }
}

public class ConfigFactsTests
{
    private static AdapterResult Build(string json, string path) => AdapterTestKit.Build(path, "config", json);

    [Fact]
    public void EachSetting_IsAConfigKeyEntity_NamedAsTheApplicationReadsIt_WithItsValueAndItsFile()
    {
        var r = Build("{ \"Ofac\": { \"TimeoutSeconds\": 15 }, \"Servers\": [\"a\", \"b\"] }", "config/appsettings.PROD.json");
        Assert.Equal(new[] { "Ofac:TimeoutSeconds", "Servers" }, r.Facts!.Entities.Select(e => e.Name));
        Assert.All(r.Facts.Entities, e => Assert.Equal("config-key", e.Type));
        var timeout = r.Facts.Entities[0];
        Assert.Equal(r.Chunks[0].Locator, timeout.Locator);
        var value = JsonSerializer.SerializeToElement(timeout.Attrs!["values"])[0];
        Assert.Equal(("config/appsettings.PROD.json", "15"), (value.GetProperty("file").GetString(), value.GetProperty("value").GetString()));
        Assert.Equal("Read 2 settings.", r.Summary);
    }

    [Fact]
    public void ASecretValue_NeverReachesAFact()
    {
        var r = Build("{ \"Db\": { \"Password\": \"hunter2\", \"Conn\": \"Server=x;Password=hunter2\" } }", "config/a.json");
        var json = r.Facts!.ToJson();
        Assert.DoesNotContain("hunter2", json);
        // Read back, not searched in the text: JSON writes "<" as an escape.
        var values = FactSet.FromJson(json).Entities
            .Select(e => JsonSerializer.SerializeToElement(e.Attrs!["values"])[0].GetProperty("value").GetString())
            .ToList();
        Assert.Contains("<<MASKED>>", values);
        Assert.Contains("Server=x;Password=<<MASKED>>", values);
    }

    [Fact]
    public void TheSameSettingInTwoEnvironments_KeepsBothValues_AndBothPiecesOfEvidence()
    {
        // JSON writes each backslash of \\vault-dev\exports\ twice.
        var dev = Build("""{ "Reconciliation": { "VaultFilePath": "\\\\vault-dev\\exports\\" } }""", "config/appsettings.DEV.json");
        var prod = Build("""{ "Reconciliation": { "VaultFilePath": "\\\\vault-prd\\export\\" } }""", "config/appsettings.PROD.json");

        var graph = GraphMerger.Merge(new[]
        {
            new FileFactSet("config/appsettings.DEV.json", dev.Facts!, new Dictionary<string, string> { [dev.Chunks[0].Locator] = "CFG-11111111" }),
            new FileFactSet("config/appsettings.PROD.json", prod.Facts!, new Dictionary<string, string> { [prod.Chunks[0].Locator] = "CFG-22222222" }),
        });

        var key = Assert.Single(graph.Entities);
        Assert.Equal("Reconciliation:VaultFilePath", key.Name);
        Assert.Equal(new[] { "CFG-11111111", "CFG-22222222" }, key.EvidenceIds);
        var values = key.Attrs["values"].EnumerateArray().Select(v => (v.GetProperty("file").GetString(), v.GetProperty("value").GetString())).ToList();
        Assert.Equal(new[]
        {
            ("config/appsettings.DEV.json", @"\\vault-dev\exports\"),
            ("config/appsettings.PROD.json", @"\\vault-prd\export\"),
        }, values);
    }

    [Fact]
    public void AConfigKeyAPackageRefersTo_MeetsTheSettingThatDefinesIt()
    {
        var config = Build("{ \"Reconciliation\": { \"VaultFilePath\": \"x\" } }", "config/appsettings.PROD.json");
        var package = new FactSet(
            Array.Empty<EntityFact>(),
            new[] { new LinkFact("configures", new("config-key", "Reconciliation:VaultFilePath"), new("package", "DrawerRecon"), "xpath:/Executable", "medium") });

        var graph = GraphMerger.Merge(new[]
        {
            new FileFactSet("config/appsettings.PROD.json", config.Facts!, new Dictionary<string, string> { [config.Chunks[0].Locator] = "CFG-22222222" }),
            new FileFactSet("batch/ssis/DrawerRecon.dtsx", package, new Dictionary<string, string> { ["xpath:/Executable"] = "ETL-33333333" }),
        });

        var key = Assert.Single(graph.Entities, e => e.Type == "config-key");
        Assert.True(key.Declared);
        Assert.Equal(new[] { "CFG-22222222" }, key.EvidenceIds);
        var link = Assert.Single(graph.Links);
        Assert.Same(key, link.From);
        Assert.False(link.To.Declared);
    }
}
