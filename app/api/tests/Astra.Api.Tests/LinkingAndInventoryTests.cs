using System.Text.Json;
using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The rules that join separate files' facts, the way evidence is found for a line of code, and the inventory counted from the graph.</summary>
public class LinkingAndInventoryTests
{
    private static EntityFact Setting(string key, params (string File, string Value)[] values) =>
        new(EntityVocabulary.ConfigKey, key, null, Attrs.Of(("values", values.Select(v => new { file = v.File, value = v.Value }).ToList())));

    private static FileFactSet File(string path, FactSet facts) => new(path, facts, new Dictionary<string, string>());

    private static FactSet Entities(params EntityFact[] e) => new(e, Array.Empty<LinkFact>());

    private static MergedEntity Find(MergedGraph g, string type, string name) =>
        g.Entities.Single(e => e.Type == type && e.Key == GraphMerger.KeyOf(name));

    private static MergedGraph Merge(params FileFactSet[] files) => GraphMerger.Merge(files);

    // ── setting ↔ address ↔ endpoint ────────────────────────────────────

    [Fact]
    public void Endpoint_WhoseBaseIsASetting_ListsTheSettingsValueInEveryEnvironment()
    {
        var graph = Merge(
            File("src/Client.cs", Entities(new EntityFact(EntityVocabulary.Endpoint, "POST {Ofac:BaseUrl}/screen", null, Attrs.Of(("baseKey", "Ofac:BaseUrl"))))),
            File("config/DEV.json", Entities(Setting("Ofac:BaseUrl", ("config/DEV.json", "https://ofac-dev.brfc.example/")))),
            File("config/PROD.json", Entities(Setting("Ofac:BaseUrl", ("config/PROD.json", "https://ofac.brfc.example/")))));

        var applied = LinkingRules.Apply(graph, Array.Empty<NarrativeChunk>());
        var resolved = Find(applied, "endpoint", "POST {Ofac:BaseUrl}/screen").Attrs["resolved"];
        var values = resolved.EnumerateArray().Select(v => (v.GetProperty("file").GetString(), v.GetProperty("value").GetString())).ToArray();
        Assert.Equal(2, values.Length);
        Assert.Contains(("config/PROD.json", "https://ofac.brfc.example/"), values);
    }

    [Fact]
    public void Endpoint_WithALiteralBase_IsLinkedToASettingThatHoldsTheSameAddress()
    {
        var graph = Merge(
            File("src/Loan.cs", Entities(new EntityFact(EntityVocabulary.Endpoint, "GET https://loanserv.brfc.example/api/loans/{r}", null,
                Attrs.Of(("baseUrl", "https://loanserv.brfc.example"))))),
            File("config/PROD.json", Entities(
                Setting("LoanLookup:BaseUrl", ("config/PROD.json", "https://LoanServ.brfc.example/")),
                Setting("Other:Url", ("config/PROD.json", "https://other.brfc.example/")))));

        var applied = LinkingRules.Apply(graph, Array.Empty<NarrativeChunk>());
        var link = Assert.Single(applied.Links, l => l.Relation == "configures");
        Assert.Equal("LoanLookup:BaseUrl", link.From.Name);
        Assert.Equal("medium", link.Confidence);
        Assert.Equal("config-url", link.Attrs["rule"].GetString());
        Assert.Equal("rule", link.Origin);
    }

    [Fact]
    public void Queue_NamedFromASetting_IsLinkedToItAndResolved()
    {
        var graph = Merge(
            File("src/Pub.cs", Entities(
                new EntityFact(EntityVocabulary.Queue, "{RabbitMq:Exchange}"),
                new EntityFact(EntityVocabulary.Queue, "gl.ack"))),
            File("config/PROD.json", Entities(
                Setting("RabbitMq:Exchange", ("config/PROD.json", "tlr.gl")),
                Setting("RabbitMq:AckQueue", ("config/PROD.json", "gl.ack")))));

        var applied = LinkingRules.Apply(graph, Array.Empty<NarrativeChunk>());
        var byName = applied.Links.Where(l => l.Relation == "configures").ToDictionary(l => l.To.Name);
        Assert.Equal("high", byName["{RabbitMq:Exchange}"].Confidence);
        Assert.Equal("medium", byName["gl.ack"].Confidence);
        Assert.Equal("RabbitMq:AckQueue", byName["gl.ack"].From.Name);
        Assert.Equal("tlr.gl", Find(applied, "queue", "{RabbitMq:Exchange}").Attrs["resolved"][0].GetProperty("value").GetString());
    }

    // ── mentions ────────────────────────────────────────────────────────

    private static FactSet Estate() => new(
        new[]
        {
            new EntityFact(EntityVocabulary.Job, "TLR_EOD_POST"),
            new EntityFact(EntityVocabulary.Procedure, "usp_PurgeStage"),
            new EntityFact(EntityVocabulary.Table, "TLR_STAGE", null, Attrs.Of(
                ("uniqueKeys", new[] { new { name = "UQ_TLR_STAGE_TXN_REF", columns = new[] { "TXN_REF" } } }))),
            new EntityFact(EntityVocabulary.Table, "Drawer"),
            new EntityFact(EntityVocabulary.Class, "Teller.Services.OfacScreeningClient"),
            new EntityFact(EntityVocabulary.ConfigKey, "Ofac:TimeoutSeconds"),
        },
        Array.Empty<LinkFact>());

    private static JsonElement Mentions(MergedGraph g, string type, string name) =>
        Find(g, type, name).Attrs.TryGetValue("mentions", out var m) ? m : default;

    [Fact]
    public void TextThatNamesAThing_IsListedOnItAsNarrativeEvidence_WithHowItWasMatched()
    {
        var graph = Merge(File("db/ddl.sql", Estate()));
        var applied = LinkingRules.Apply(graph, new[]
        {
            new NarrativeChunk("TKT-1", "ticket", "number: INC-T-0412\ncmdb_ci: TLR_EOD_POST\ndescription: failed with 2627, UQ_TLR_STAGE_TXN_REF violated"),
            new NarrativeChunk("TRN-1", "transcript", "Dale: then you run usp_PurgeStage, dbo.TLR_STAGE is emptied, and the Drawer is fine."),
            new NarrativeChunk("DOC-1", "document", "Set Ofac.TimeoutSeconds higher; see Teller.Services.OfacScreeningClient."),
            new NarrativeChunk("TKT-2", "ticket", "cmdb_ci: Teller printer\ndescription: nothing to do with any of it"),
        });

        var job = Mentions(applied, "job", "TLR_EOD_POST").EnumerateArray().ToList();
        var jobMention = Assert.Single(job);
        Assert.Equal("TKT-1", jobMention.GetProperty("evidenceId").GetString());
        Assert.Equal("cmdb_ci", jobMention.GetProperty("via").GetString());

        // A constraint the table owns names the table; dbo.TLR_STAGE names it too.
        var stage = Mentions(applied, "table", "TLR_STAGE").EnumerateArray()
            .Select(m => (m.GetProperty("evidenceId").GetString(), m.TryGetProperty("via", out var via) ? via.GetString() : null))
            .ToList();
        Assert.Contains(("TKT-1", "constraint UQ_TLR_STAGE_TXN_REF"), stage);
        Assert.Contains(("TRN-1", (string?)null), stage);

        Assert.Equal("TRN-1", Assert.Single(Mentions(applied, "procedure", "usp_PurgeStage").EnumerateArray()).GetProperty("evidenceId").GetString());
        Assert.Equal("DOC-1", Assert.Single(Mentions(applied, "config-key", "Ofac:TimeoutSeconds").EnumerateArray()).GetProperty("evidenceId").GetString());
        Assert.Equal("DOC-1", Assert.Single(Mentions(applied, "class", "Teller.Services.OfacScreeningClient").EnumerateArray()).GetProperty("evidenceId").GetString());

        // "Drawer" is a plain word: a sentence containing it says nothing about the table.
        Assert.False(Find(applied, "table", "Drawer").Attrs.ContainsKey("mentions"));
        Assert.Equal(1, Find(applied, "job", "TLR_EOD_POST").Attrs["mentionCounts"].GetProperty("ticket").GetInt32());
    }

    [Fact]
    public void Mentions_AreCappedPerKind_ButCountedInFull()
    {
        var graph = Merge(File("db/ddl.sql", Estate()));
        var tickets = Enumerable.Range(1, 40).Select(i => new NarrativeChunk($"TKT-{i}", "ticket", "TLR_EOD_POST failed again")).ToList();
        var applied = LinkingRules.Apply(graph, tickets);

        Assert.Equal(LinkingRules.MaxMentionsPerKind, Mentions(applied, "job", "TLR_EOD_POST").GetArrayLength());
        Assert.Equal(40, Find(applied, "job", "TLR_EOD_POST").Attrs["mentionCounts"].GetProperty("ticket").GetInt32());
    }

    [Fact]
    public void TheRules_GiveTheSameResultEveryTime()
    {
        FileFactSet[] Files() => new[] { File("db/ddl.sql", Estate()) };
        var narrative = new[] { new NarrativeChunk("TKT-1", "ticket", "cmdb_ci: TLR_EOD_POST usp_PurgeStage") };
        string Dump(MergedGraph g) => string.Join("|", g.Entities.Select(e => e.Key + JsonSerializer.Serialize(e.Attrs)));
        Assert.Equal(Dump(LinkingRules.Apply(Merge(Files()), narrative)), Dump(LinkingRules.Apply(Merge(Files()), narrative)));
    }

    [Theory]
    [InlineData("TLR_EOD_POST", true)]
    [InlineData("usp_PurgeStage", true)]
    [InlineData("Ofac:TimeoutSeconds", true)]
    [InlineData("OfacScreeningClient", true)]
    [InlineData("tlr.gl", true)]
    [InlineData("TXN2", false)]
    [InlineData("Drawer", false)]
    [InlineData("payment", false)]
    [InlineData("a_b", false)]
    public void Distinctive_SaysWhichNamesAreSpecificEnoughToFindInASentence(string alias, bool expected) =>
        Assert.Equal(expected, LinkingRules.Distinctive(alias));

    // ── evidence for a line of code ─────────────────────────────────────

    private static FileFactSet CodeFile(params (int From, int To, string Id)[] spans) =>
        new("src/A.cs", FactSet.Empty, new Dictionary<string, string> { ["L1-1"] = "SRC-exact" },
            spans.Select(s => new ChunkSpan(s.From, s.To, s.Id)).ToList());

    [Fact]
    public void Evidence_ForALineRange_IsTheSmallestRoutineContainingIt()
    {
        var f = CodeFile((10, 100, "SRC-big"), (20, 40, "SRC-inner"), (50, 60, "SRC-other"));
        Assert.Equal("SRC-inner", f.EvidenceFor("L25-26"));
        Assert.Equal("SRC-big", f.EvidenceFor("L70-72"));
        Assert.Equal("SRC-exact", f.EvidenceFor("L1-1"));
    }

    [Fact]
    public void Evidence_ForARangeNoRoutineContains_IsTheOneItOverlapsMost_OrTheOneJustBelow()
    {
        var f = CodeFile((10, 20, "SRC-a"), (21, 40, "SRC-b"));
        Assert.Equal("SRC-b", f.EvidenceFor("L18-35"));

        // A class header above its first method is shown by that method; far above, by nothing.
        var g = CodeFile((20, 30, "SRC-first"));
        Assert.Equal("SRC-first", g.EvidenceFor("L12-14"));
        Assert.Null(CodeFile((60, 70, "SRC-far")).EvidenceFor("L1-3"));
        Assert.Null(g.EvidenceFor("t=00:01:00-00:02:00"));
    }

    // ── inventory ───────────────────────────────────────────────────────

    private static InventoryEntity E(string type, string name, bool declared = true, params string[] evidence) =>
        new($"{type}:{name}", type, name, declared, evidence);

    private static InventoryLink L(string relation, string from, string to) => new(relation, from, to);

    [Fact]
    public void Measures_CountDeclaredThings_AndSetTheExpectedCountBesideThem()
    {
        var entities = new[]
        {
            E("table", "A"), E("table", "B"), E("table", "Ghost", declared: false),
            E("job", "J1"), E("job", "J2"),
            E("endpoint", "GET /x"), E("queue", "q1"),
            E("file", "D:/drop.csv"), E("file", "D:/unused.csv"),
            E("job", "J3"),
        };
        var links = new[] { L("reads", "job:J3", "file:D:/drop.csv") };
        var measures = InventoryRules.Measures(entities, links, 120,
            new Dictionary<string, int> { ["jobs"] = 3, ["interfaces"] = 8, ["owners"] = 4 }).ToDictionary(m => m.Key);

        Assert.Equal(2, measures["tables"].Found);          // the one only mentioned is not counted
        Assert.Equal(3, measures["jobs"].Found);
        Assert.Equal(3, measures["jobs"].Expected);
        Assert.Equal(120, measures["routines"].Found);
        Assert.Equal(3, measures["interfaces"].Found);      // endpoint, queue, and the file a job reads; not the unused file
        Assert.Equal(8, measures["interfaces"].Expected);
        Assert.Null(measures["owners"].Found);              // expected, but nothing here can count it
        Assert.Equal(4, measures["owners"].Expected);
    }

    [Fact]
    public void Findings_NameWhatIsReferencedButNotDefined_AndSettingsThatDoNotMeetTheCode()
    {
        var entities = new[]
        {
            E("procedure", "usp_Gone", declared: false, "SQL-1"),
            E("job", "TLR_X", declared: false),
            E("file", "D:/x.csv", declared: false),
            E("class", "Svc"),
            E("config-key", "Read:Missing", declared: false),
            E("config-key", "Read:Present"),
            E("config-key", "Never:Read"),
            E("config-key", "Configures:Endpoint"),
        };
        var links = new[]
        {
            L("calls", "class:Svc", "procedure:usp_Gone"),
            L("reads", "class:Svc", "config-key:Read:Missing"),
            L("reads", "class:Svc", "config-key:Read:Present"),
            L("configures", "config-key:Configures:Endpoint", "endpoint:GET /x"),
        };

        var findings = InventoryRules.Findings(entities, links).ToDictionary(f => f.Code);
        Assert.Equal(new[] { "job:TLR_X", "procedure:usp_Gone" }, findings["referenced-not-defined"].Items.Select(i => i.Ref).ToArray());
        Assert.Equal(new[] { "SQL-1" }, findings["referenced-not-defined"].Items.Single(i => i.Name == "usp_Gone").EvidenceIds.ToArray());
        Assert.Equal("config-key:Read:Missing", Assert.Single(findings["setting-not-configured"].Items).Ref);
        Assert.Equal("config-key:Never:Read", Assert.Single(findings["setting-never-read"].Items).Ref);
    }

    [Fact]
    public void Findings_DoNotSayASettingIsNeverRead_WhenNothingAttachedCouldHaveRead()
    {
        var entities = new[] { E("config-key", "Some:Key"), E("table", "T") };
        Assert.DoesNotContain(InventoryRules.Findings(entities, Array.Empty<InventoryLink>()), f => f.Code == "setting-never-read");
        Assert.Empty(InventoryRules.Findings(Array.Empty<InventoryEntity>(), Array.Empty<InventoryLink>()));
    }
}
