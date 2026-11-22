using System.Text.Json;
using Astra.Api.Docs;
using Astra.Api.Evidence;
using Astra.Api.Knowledge;
using Astra.Api.Llm;
using Microsoft.Extensions.Options;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// Claims: what people said, cut into atomic claims and checked against what the structured evidence states. The
/// linker, the join to written statements and the ticket clusters are pure; the extractor is driven by a writer
/// that answers as the test says.
/// </summary>
public class ClaimTests
{
    // ── the graph the claims are checked against ────────────────────────

    private static GraphNode Node(string type, string name, string[] evidence, object? attrs = null, bool declared = true) =>
        new(Guid.NewGuid(), $"{type}:{name}", type, type, name, declared,
            attrs is null ? null : JsonSerializer.SerializeToElement(attrs), evidence, Array.Empty<string>());

    private static GraphEdge Edge(string relation, string from, string to, params string[] evidence) =>
        new(relation, relation, from, to, "high", "file", null, evidence);

    private static readonly EntityGraphDto Graph = new(
        new[]
        {
            Node("job", "TLR_EOD_POST", new[] { "JOB-00000001" }, new { schedule = "23:30" }),
            Node("job", "TLR_EOD_EXTRACT", new[] { "JOB-00000002" }),
            Node("table", "TLR_STAGE", new[] { "SQL-00000003" }),
            Node("config-key", "Ofac:TimeoutSeconds", new[] { "CFG-00000004" }, new { values = new[] { new { file = "appsettings.json", value = "30" } } }),
        },
        new[]
        {
            Edge("writes", "job:TLR_EOD_POST", "table:TLR_STAGE", "JOB-00000001", "SQL-00000003"),
            Edge("precedes", "job:TLR_EOD_EXTRACT", "job:TLR_EOD_POST", "JOB-00000005"),
        },
        Array.Empty<GraphTypeCount>());

    private static readonly IEvidenceLookup Exists = new IdSetEvidence(new HashSet<string>(new[]
    {
        "JOB-00000001", "JOB-00000002", "JOB-00000005", "SQL-00000003", "CFG-00000004", "TKT-0000000d", "TRN-00000009",
    }));

    private static Claim ClaimOf(string text, string evidence, params ClaimAssertion[] assertions) =>
        new(ClaimIds.Stable(text, evidence), text, evidence, null, assertions);

    private static ClaimVerdict Judge(Claim c) => ClaimLinker.Link(new[] { c }, Graph, Exists).Single();

    // ── the linker ──────────────────────────────────────────────────────

    [Fact]
    public void AClaimTheStructuredEvidenceStates_IsSupported_AndNamesThatEvidence()
    {
        var v = Judge(ClaimOf("TLR_EOD_POST writes TLR_STAGE.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "writes", "TLR_STAGE")));
        Assert.Equal(StatementStatus.Supported, v.Status);
        Assert.Contains("JOB-00000001", v.StructuredEvidenceIds);
        Assert.Contains("job:TLR_EOD_POST", v.Entities);
    }

    [Fact]
    public void AnAttributeWithTheStatedValue_IsSupported_AndADifferentOneIsAConflictNamingBothSides()
    {
        var right = Judge(ClaimOf("The EOD post runs at 23:30.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "23:30")));
        Assert.Equal(StatementStatus.Supported, right.Status);

        var wrong = Judge(ClaimOf("The EOD post runs at 22:00.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "22:00")));
        Assert.Equal(StatementStatus.Conflict, wrong.Status);
        Assert.Equal(new[] { "JOB-00000001" }, wrong.StructuredEvidenceIds);
        Assert.Contains("22:00", wrong.Reason);
        Assert.Contains("23:30", wrong.Reason);
    }

    [Fact]
    public void ASettingValue_IsReadFromTheValuePerFile()
    {
        Assert.Equal(StatementStatus.Supported,
            Judge(ClaimOf("The OFAC timeout is 30 seconds.", "TRN-00000009", new ClaimAssertion("Ofac:TimeoutSeconds", "attribute", "value", "30"))).Status);
        Assert.Equal(StatementStatus.Conflict,
            Judge(ClaimOf("The OFAC timeout is 90 seconds.", "TRN-00000009", new ClaimAssertion("Ofac:TimeoutSeconds", "attribute", "value", "90"))).Status);
    }

    [Fact]
    public void TheOtherOrderOfTwoJobs_IsAConflict_ButAnUnstatedRelationIsOnlyUnverified()
    {
        var reversed = Judge(ClaimOf("The post runs before the extract.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "precedes", "TLR_EOD_EXTRACT")));
        Assert.Equal(StatementStatus.Conflict, reversed.Status);
        Assert.Contains("JOB-00000005", reversed.StructuredEvidenceIds);

        var unstated = Judge(ClaimOf("The extract reads the stage table.", "TRN-00000009", new ClaimAssertion("TLR_EOD_EXTRACT", "reads", "TLR_STAGE")));
        Assert.Equal(StatementStatus.Unverified, unstated.Status);
    }

    [Fact]
    public void AClaimNothingCanCheck_IsUnverified_WithAReason()
    {
        Assert.Equal(StatementStatus.Unverified, Judge(ClaimOf("Call Dana if it fails.", "TKT-0000000d")).Status);

        var unknown = Judge(ClaimOf("NOPE_JOB writes TLR_STAGE.", "TKT-0000000d", new ClaimAssertion("NOPE_JOB", "writes", "TLR_STAGE")));
        Assert.Equal(StatementStatus.Unverified, unknown.Status);
        Assert.Contains("not in the inventory", unknown.Reason);

        var noValue = Judge(ClaimOf("The extract has a schedule.", "TKT-0000000d", new ClaimAssertion("TLR_EOD_EXTRACT", "attribute", "schedule", "01:00")));
        Assert.Equal(StatementStatus.Unverified, noValue.Status);
    }

    [Fact]
    public void AConflictNeedsEvidenceOnBothSides_OtherwiseItIsNotRecorded()
    {
        var evidenceMissing = new IdSetEvidence(new HashSet<string>(new[] { "TRN-00000009" }));
        var claim = ClaimOf("The EOD post runs at 22:00.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "22:00"));
        var v = ClaimLinker.Link(new[] { claim }, Graph, evidenceMissing).Single();
        Assert.Equal(StatementStatus.Unverified, v.Status);
        Assert.Contains("not recorded", v.Reason);

        // The claim cannot be its own other side.
        var selfCited = ClaimOf("The EOD post runs at 22:00.", "JOB-00000001", new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "22:00"));
        Assert.Equal(StatementStatus.Unverified, ClaimLinker.Link(new[] { selfCited }, Graph, Exists).Single().Status);
    }

    [Fact]
    public void ALongerClaim_IsSupportedOnlyWhenEveryAssertionIs_AndAConflictWins()
    {
        var supported = new ClaimAssertion("TLR_EOD_POST", "writes", "TLR_STAGE");
        var open = new ClaimAssertion("TLR_EOD_EXTRACT", "reads", "TLR_STAGE");
        var wrong = new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "22:00");
        Assert.Equal(StatementStatus.Unverified, Judge(ClaimOf("two", "TRN-00000009", supported, open)).Status);
        Assert.Equal(StatementStatus.Conflict, Judge(ClaimOf("three", "TRN-00000009", supported, open, wrong)).Status);
    }

    // ── joining claims to written statements ────────────────────────────

    private static StatementItem Item(string id, string text, params string[] cites) => new(id, "runbook:x", text, cites);

    private static StatementDocument Doc(params StatementItem[] items) =>
        new("runbook", "t", new[] { new StatementSection("s", items) }, Array.Empty<StatementConflict>(), Array.Empty<OpenQuestion>());

    [Fact]
    public void AStatementThatRestsOnASupportedClaim_IsVerified_AndOneOnAContradictedClaimCarriesAConflict()
    {
        var good = ClaimOf("TLR_EOD_POST writes TLR_STAGE.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "writes", "TLR_STAGE"));
        var bad = ClaimOf("The EOD post runs at 22:00.", "TKT-0000000d", new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "22:00"));
        var verdicts = ClaimLinker.Link(new[] { good, bad }, Graph, Exists);

        var doc = Doc(
            Item("st-1", "`TLR_EOD_POST` writes to `TLR_STAGE`.", "TRN-00000009"),
            Item("st-2", "`TLR_EOD_POST` starts at 22:00.", "TKT-0000000d"),
            Item("st-3", "`TLR_EOD_POST` writes to `TLR_STAGE`.", "SQL-00000003"));
        var found = ClaimIndex.Apply(doc, new[] { good, bad }, verdicts);

        Assert.Equal(new[] { "st-1" }, found.VerifiedItemIds.ToArray());
        var conflict = Assert.Single(found.Conflicts);
        Assert.Equal("st-2", conflict.ItemId);
        Assert.Equal(new[] { "TKT-0000000d", "JOB-00000001" }, conflict.EvidenceIds);
    }

    [Fact]
    public void AStatementMustNameWhatTheClaimIsAbout_ToBeMatchedToIt()
    {
        var claim = ClaimOf("TLR_EOD_POST writes TLR_STAGE.", "TRN-00000009", new ClaimAssertion("TLR_EOD_POST", "writes", "TLR_STAGE"));
        var verdicts = ClaimLinker.Link(new[] { claim }, Graph, Exists);
        var found = ClaimIndex.Apply(Doc(Item("st-1", "The posting job writes a staging table.", "TRN-00000009")), new[] { claim }, verdicts);
        Assert.Empty(found.VerifiedItemIds);
    }

    [Fact]
    public void TheFindings_FeedTheGate_SoAVerifiedStatementIsSupported_AndAConflictIsRecorded()
    {
        var claim = ClaimOf("The EOD post runs at 22:00.", "TKT-0000000d", new ClaimAssertion("TLR_EOD_POST", "attribute", "schedule", "22:00"));
        var verdicts = ClaimLinker.Link(new[] { claim }, Graph, Exists);
        var doc = Doc(Item("st-2", "`TLR_EOD_POST` starts at 22:00.", "TKT-0000000d"));
        var found = ClaimIndex.Apply(doc, new[] { claim }, verdicts);

        var gated = GroundingGate.Evaluate(
            doc with { Conflicts = found.Conflicts },
            new DictionaryEvidence(new[]
            {
                new GateChunk("TKT-0000000d", "ticket", "narrative", "t", "TLR_EOD_POST starts at 22:00"),
                new GateChunk("JOB-00000001", "job", "structured", "j", "TLR_EOD_POST schedule 23:30"),
            }),
            new Dictionary<string, string> { ["st-2"] = SupportVerdict.Yes },
            found.VerifiedItemIds);
        Assert.Equal(StatementStatus.Conflict, gated.Document.Items.Single().Status);
    }

    // ── ticket clusters ─────────────────────────────────────────────────

    private static TicketText T(string n, string text) => new($"TKT-{n}", $"INC-{n}", text);

    private static readonly string[] Routine = { "password", "printer" };

    [Fact]
    public void TicketsSharingAnErrorCode_FormACluster_ThreeOrMoreIsACandidate()
    {
        var result = TicketClusterer.Cluster(new[]
        {
            T("0001", "TLR_EOD_POST failed with error 2627 on the staged payments"),
            T("0002", "Nightly posting stopped, SQL error 2627 duplicate key"),
            T("0003", "EOD run: error 2627, duplicate TXN_REF in stage"),
            T("0004", "Teller cannot print receipts, error 4410 on the till"),
        }, Routine);

        var cluster = result.Clusters.First();
        Assert.Equal("2627", cluster.Key);
        Assert.Equal(3, cluster.EvidenceIds.Count);
        Assert.True(cluster.Candidate);
        Assert.False(result.Clusters.Last().Candidate);
    }

    [Fact]
    public void RoutineRequests_AreTaggedByKeyword_AndKeptOutOfEveryCluster()
    {
        var result = TicketClusterer.Cluster(new[]
        {
            T("0001", "Please reset my password"),
            T("0002", "The printer on floor two is jammed"),
            T("0003", "TLR_EOD_POST failed with error 2627"),
        }, Routine);

        Assert.Equal(new[] { "TKT-0001", "TKT-0002" }, result.Routine);
        Assert.Single(result.Clusters);
        Assert.DoesNotContain(result.Clusters.SelectMany(c => c.EvidenceIds), id => result.Routine.Contains(id));
    }

    [Fact]
    public void ColumnNamesOfATicketRecord_AreNotThingsInTheSystem()
    {
        // The ticket adapter's chunk text is "column: value" per line; every ticket has the same columns.
        static TicketText Rec(string n, string title, string description) =>
            new($"TKT-{n}", $"INC-{n}", $"number: INC-{n}\nopened_at: 2026-03-02\nshort_description: {title}\ndescription: {description}");

        var result = TicketClusterer.Cluster(new[]
        {
            Rec("0001", "Password reset", "Please reset my password, locked out after the weekend."),
            Rec("0002", "Printer jam", "The printer on floor two is jammed again."),
            Rec("0003", "Posting fails", "TLR_EOD_POST failed with error 2627 on the staged rows."),
            Rec("0004", "Posting stopped", "Nightly posting stopped with SQL error 2627, duplicate key."),
            Rec("0005", "Disk full", "The file server share is out of space again for the nightly extract."),
        }, Routine);

        Assert.Equal(new[] { "TKT-0001", "TKT-0002" }, result.Routine);
        Assert.Equal(new[] { "2627", }, result.Clusters.Where(c => c.EvidenceIds.Count > 1).Select(c => c.Key).ToArray());
        Assert.Equal(2, result.Clusters.Single(c => c.Key == "2627").EvidenceIds.Count);
        Assert.Contains(result.Clusters, c => c.EvidenceIds.SequenceEqual(new[] { "TKT-0005" }));
    }

    [Fact]
    public void ATicketThatNamesSomethingInTheSystem_IsNotRoutine_EvenWithAKeyword()
    {
        var result = TicketClusterer.Cluster(new[] { T("0001", "TLR_EOD_POST password for the service account expired") }, Routine);
        Assert.Empty(result.Routine);
    }

    [Fact]
    public void ClusteringIsDeterministic_WhateverTheOrderTicketsArriveIn()
    {
        var tickets = new[]
        {
            T("0003", "EOD run: error 2627, duplicate TXN_REF in stage"),
            T("0001", "TLR_EOD_POST failed with error 2627"),
            T("0002", "Nightly posting stopped, SQL error 2627 duplicate key"),
        };
        var a = JsonSerializer.Serialize(TicketClusterer.Cluster(tickets, Routine));
        var b = JsonSerializer.Serialize(TicketClusterer.Cluster(tickets.Reverse().ToArray(), Routine));
        Assert.Equal(a, b);
    }

    // ── the extractor ───────────────────────────────────────────────────

    private sealed class ScriptedWriter : IDocWriter
    {
        private readonly Func<DocWriteRequest, string> _answer;
        public List<DocWriteRequest> Requests { get; } = new();
        public ScriptedWriter(Func<DocWriteRequest, string> answer) { _answer = answer; }
        public string ProviderName => "test";
        public bool IsMock => false;
        public string ConfigVersion => "test";

        public Task<DocWriteResult> WriteAsync(DocWriteRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new DocWriteResult(_answer(request), new AnthropicUsage(10, 5, 0, 0, request.Model), request.Model, 1, "end_turn"));
        }
    }

    private static ClaimExtractor Extractor(IDocWriter writer, IStatementFixtures? fixtures = null) =>
        new(writer, Options.Create(new DocsOptions()), Options.Create(new KnowledgeOptions()),
            new StatementPrompts("W", "S", "CLAIMS PROMPT"), fixtures ?? new NoStatementFixtures());

    private static readonly GateChunk Turn = new("TRN-00000009", "transcript", "narrative", "00:14:32",
        "When `TLR_EOD_POST` fails with 2627 you delete the second staged row and restart from step 3.");

    [Fact]
    public async Task TheExtractor_AsksOncePerBatch_AtTemperatureZero_WithAReplayKey()
    {
        var writer = new ScriptedWriter(_ => JsonSerializer.Serialize(new { claims = Array.Empty<object>() }));
        var chunks = Enumerable.Range(0, ClaimExtractor.BatchSize + 1)
            .Select(i => new GateChunk($"TRN-0000000{i}", "transcript", "narrative", "t", "text")).ToList();
        var result = await Extractor(writer).ExtractAsync(chunks, CancellationToken.None);

        Assert.Equal(2, writer.Requests.Count);
        Assert.All(writer.Requests, r => Assert.Equal(0, r.Temperature));
        Assert.Equal(new[] { "claims:extract:1", "claims:extract:2" }, writer.Requests.Select(r => r.ReplayKey));
        Assert.Equal("CLAIMS PROMPT", writer.Requests[0].SystemBlocks.Single());
        Assert.Equal(2, result.Calls.Count);
    }

    [Fact]
    public async Task AClaimIsKept_OnlyIfItCameFromShownEvidence_AndEveryNameInItIsInThatEvidence()
    {
        var answer = JsonSerializer.Serialize(new
        {
            claims = new object[]
            {
                new
                {
                    text = "When `TLR_EOD_POST` fails with 2627 the second staged row is deleted.", evidence = "[trn-00000009]", at = "00:14:32",
                    assertions = new object[]
                    {
                        new { subject = "TLR_EOD_POST", predicate = "writes", @object = "TLR_STAGE" },
                        new { subject = "TLR_EOD_POST", predicate = "dances", @object = "TLR_STAGE" },
                    },
                },
                new { text = "`TLR_NIGHTLY_MAGIC` fixes it.", evidence = "TRN-00000009" },
                new { text = "Restart from step 3.", evidence = "TRN-0000FFFF" },
                new { text = "", evidence = "TRN-00000009" },
            },
        });
        var result = await Extractor(new ScriptedWriter(_ => answer)).ExtractAsync(new[] { Turn }, CancellationToken.None);

        var claim = Assert.Single(result.Claims);
        Assert.Equal("TRN-00000009", claim.EvidenceId);
        Assert.Equal("00:14:32", claim.At);
        var assertion = Assert.Single(claim.Assertions);
        Assert.Equal("writes", assertion.Predicate);
        Assert.Equal(2, result.Dropped.Count);
        Assert.Contains(result.Dropped, d => d.Contains("TLR_NIGHTLY_MAGIC"));
        Assert.Contains(result.Dropped, d => d.Contains("not shown"));
        Assert.Contains(result.Notes, n => n.Contains("dances"));
    }

    [Fact]
    public async Task AFailedBatch_IsReportedWithTheProvidersReason_AndTheOthersStillCount()
    {
        var n = 0;
        var writer = new ScriptedWriter(_ =>
        {
            if (++n == 1) throw new InvalidOperationException("429 rate limited");
            return JsonSerializer.Serialize(new { claims = new[] { new { text = "Restart from step 3 of `TLR_EOD_POST`.", evidence = "TRN-00000001" } } });
        });
        var chunks = Enumerable.Range(0, ClaimExtractor.BatchSize + 1)
            .Select(i => new GateChunk($"TRN-0000000{i}", "transcript", "narrative", "t", "Restart from step 3 of TLR_EOD_POST.")).ToList();
        var result = await Extractor(writer).ExtractAsync(chunks, CancellationToken.None);

        Assert.Contains(result.Notes, x => x.Contains("429 rate limited"));
        Assert.Empty(result.Claims); // the second batch cites an item from the first, which it was not shown
        Assert.Single(result.Calls);
    }

    [Fact]
    public void TheMockAnswer_GivesOneClaimPerLineThatNamesSomething_AndNothingToCheck()
    {
        var doc = JsonDocument.Parse(ClaimExtractor.MockAnswer(new[] { Turn, new GateChunk("TRN-0000000a", "transcript", "narrative", "x", "Thanks everyone, see you tomorrow.") }));
        var claims = doc.RootElement.GetProperty("claims").EnumerateArray().ToList();
        Assert.Single(claims);
        Assert.Equal("TRN-00000009", claims[0].GetProperty("evidence").GetString());
        Assert.False(claims[0].TryGetProperty("assertions", out _));
    }

    [Fact]
    public async Task AFixture_ForTheReplayKey_StandsInForTheModel()
    {
        var fixtures = new FakeFixtures(new Dictionary<string, string>
        {
            ["claims:extract:1"] = JsonSerializer.Serialize(new
            {
                claims = new[] { new { text = "Delete the second staged row, then restart from step 3.", evidence = "TRN-00000009" } },
            }),
        });
        var writer = new ScriptedWriter(r => r.MockPayload());
        var result = await Extractor(writer, fixtures).ExtractAsync(new[] { Turn }, CancellationToken.None);
        Assert.Equal("Delete the second staged row, then restart from step 3.", Assert.Single(result.Claims).Text);
    }

    private sealed class FakeFixtures : IStatementFixtures
    {
        private readonly IReadOnlyDictionary<string, string> _byKey;
        public FakeFixtures(IReadOnlyDictionary<string, string> byKey) { _byKey = byKey; }
        public string? Find(string replayKey) => _byKey.GetValueOrDefault(replayKey);
    }

    [Fact]
    public void ClaimIds_AreStable_ForTheSameWordsFromTheSameEvidence()
    {
        Assert.Equal(ClaimIds.Stable("Restart  from step 3", "TRN-1"), ClaimIds.Stable("restart from STEP 3", "TRN-1"));
        Assert.NotEqual(ClaimIds.Stable("Restart from step 3", "TRN-1"), ClaimIds.Stable("Restart from step 3", "TRN-2"));
    }
}
