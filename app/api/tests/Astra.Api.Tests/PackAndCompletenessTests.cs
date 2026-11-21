using Astra.Api.Knowledge;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The pack that ships (what it produces and how it is scored), how a pack is read and refused, and the completeness score checked by hand against the worked example.</summary>
public class PackAndCompletenessTests
{
    private static Pack Shipped()
    {
        var registry = PackRegistry.Load(SourceTree.ApiRoot());
        Assert.Empty(registry.Problems);
        return Assert.IsType<Pack>(registry.Get("knowledge-transfer"));
    }

    // ── the pack that ships ─────────────────────────────────────────────

    [Fact]
    public void TheShippedPack_HasTheDeliverablesWeightsAndSlotsTheScoringIsDesignedAround()
    {
        var pack = Shipped();
        Assert.Equal(
            new[] { "overview", "context-diagram", "interface-map", "batch-sheets", "runbook", "known-errors", "environments-access-contacts", "open-questions" },
            pack.Deliverables.Select(d => d.Id).ToArray());
        Assert.Equal(100, pack.Deliverables.Sum(d => d.Weight));
        Assert.Equal(new double[] { 10, 10, 15, 15, 20, 15, 10, 5 }, pack.Deliverables.Select(d => d.Weight).ToArray());

        Assert.Equal(
            new[] { "runbook:start-of-day", "runbook:end-of-day", "runbook:failure-handling", "runbook:restart", "runbook:escalation" },
            pack.Deliverable("runbook")!.StaticSlots.Select(s => s.Key).ToArray());
        Assert.Equal("entity:job:", pack.Deliverable("batch-sheets")!.EntitySlots!.KeyPrefix);
        Assert.Equal("jobs", pack.Deliverable("batch-sheets")!.EntitySlots!.CountKey);
        Assert.All(pack.Deliverables.SelectMany(d => d.StaticSlots), s => Assert.False(string.IsNullOrWhiteSpace(s.Brief)));

        Assert.Equal(new CreditRules(1.0, 0.5, 0.0), pack.Credit);
        Assert.Equal(new DependencyBands(60, 30), pack.Bands);
        Assert.Equal(new DependencyTargets(50, 25, 10), pack.Targets);
        Assert.Equal(3, pack.TierWeights["1"]);
        Assert.Equal("contractor", pack.Term("knowledgeHolder", "knowledge holder"));
        Assert.Equal("other", pack.Term("somethingElse", "other"));
    }

    [Fact]
    public void ADeliverable_IsStoredUnderItsOwnSectionKind_NeverUnderALegacyOne()
    {
        var registry = PackRegistry.Load(SourceTree.ApiRoot());
        var runbook = registry.Deliverables.FindBySectionKind("knowledge-transfer", "d-runbook");
        Assert.Equal("Runbook", runbook!.Label);
        Assert.Equal("d-runbook", runbook.SectionKind);
        Assert.Equal("deliverable", DeliverableKinds.Scope);

        Assert.True(registry.Deliverables.IsDeliverableSectionKind("d-interface-map"));
        Assert.False(registry.Deliverables.IsDeliverableSectionKind("overview"));
        Assert.False(registry.Deliverables.IsDeliverableSectionKind("d-"));
        Assert.Null(registry.Deliverables.FindBySectionKind("knowledge-transfer", "overview"));
        Assert.Null(registry.Deliverables.FindBySectionKind("no-such-pack", "d-runbook"));
        Assert.Null(registry.Deliverables.FindBySectionKind("knowledge-transfer", "d-nothing"));
    }

    [Fact]
    public void APackThatDoesNotLoad_IsReported_AndLeavesTheOthersAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "astra-packs-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Llm", "Packs", "bad"));
            File.WriteAllText(Path.Combine(root, "Llm", "Packs", "bad", "pack.json"), """{ "id": "bad", "title": "Bad", "deliverables": [] }""");
            Directory.CreateDirectory(Path.Combine(root, "Llm", "Packs", "good"));
            File.WriteAllText(Path.Combine(root, "Llm", "Packs", "good", "pack.json"), MinimalPack("good"));

            var registry = PackRegistry.Load(root);
            Assert.Equal(new[] { "good" }, registry.All.Select(p => p.Id).ToArray());
            Assert.Contains(registry.Problems, p => p.StartsWith("bad:") && p.Contains("at least one deliverable"));
            Assert.Equal(Path.Combine(root, "Llm", "Packs", "good"), registry.DirectoryOf("good"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── reading a pack ──────────────────────────────────────────────────

    private static string MinimalPack(string id, string extraDeliverable = "") => $$"""
        { "id": "{{id}}", "title": "T", "deliverables": [
            { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "static": [ { "key": "a:one" } ] } }{{extraDeliverable}} ] }
        """;

    private static IReadOnlyList<string> Errors(string json) => PackLoader.Parse(json).Errors;

    [Fact]
    public void AMinimalPack_Loads_WithTheDefaultsForCreditBandsAndTargets()
    {
        var pack = PackLoader.Parse(MinimalPack("tiny")).Pack!;
        Assert.Equal(new CreditRules(1.0, 0.5, 0.0), pack.Credit);
        Assert.Equal(new DependencyBands(60, 30), pack.Bands);
        Assert.Equal("a:one", pack.Deliverables[0].StaticSlots[0].Key);
        Assert.Equal("a:one", pack.Deliverables[0].StaticSlots[0].Label);
    }

    [Theory]
    [InlineData("not json", "Not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{ "id": "Bad Id", "title": "T", "deliverables": [] }""", "id must be lowercase")]
    [InlineData("""{ "id": "x", "deliverables": [] }""", "title is required")]
    public void APack_ThatIsWrong_IsRefusedWithTheReason(string json, string reason) =>
        Assert.Contains(Errors(json), e => e.Contains(reason));

    [Fact]
    public void ADeliverable_IsCheckedForItsIdWeightGeneratorAndSlots()
    {
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": -1, "generator": "g", "slots": { "static": [ { "key": "a:one" } ] } } ] }"""),
            e => e.Contains("weight"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": 1, "slots": { "static": [ { "key": "a:one" } ] } } ] }"""),
            e => e.Contains("generator"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g" } ] }"""),
            e => e.Contains("has no slots"));
        Assert.Contains(Errors(MinimalPack("x", ", { \"id\": \"a\", \"label\": \"A\", \"weight\": 1, \"generator\": \"g\", \"slots\": { \"static\": [ { \"key\": \"b:two\" } ] } }")),
            e => e.Contains("listed twice"));
    }

    [Fact]
    public void Slots_AreCheckedForShapeAndUniqueness()
    {
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "static": [ { "key": "nocolon" } ] } } ] }"""),
            e => e.Contains("must look like area:name"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "static": [ { "key": "a:one" }, { "key": "a:one" } ] } } ] }"""),
            e => e.Contains("'a:one' is listed twice"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "entity": { "keyPrefix": "entity:job", "countKey": "jobs" } } } ] }"""),
            e => e.Contains("ending in a colon"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "entity": { "keyPrefix": "entity:job:" } } } ] }"""),
            e => e.Contains("countKey"));
    }

    [Fact]
    public void CreditAndBands_AreCheckedForSense()
    {
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "credit": { "approvedAllSupported": 0.4, "approvedWithUnverified": 0.9 }, "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "static": [ { "key": "a:one" } ] } } ] }"""),
            e => e.Contains("must not rise"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "credit": { "approvedAllSupported": 2 }, "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "static": [ { "key": "a:one" } ] } } ] }"""),
            e => e.Contains("between 0 and 1"));
        Assert.Contains(Errors("""{ "id": "x", "title": "T", "bands": { "highAbove": 30, "mediumFrom": 60 }, "deliverables": [ { "id": "a", "label": "A", "weight": 1, "generator": "g", "slots": { "static": [ { "key": "a:one" } ] } } ] }"""),
            e => e.Contains("mediumFrom < highAbove"));
    }

    [Theory]
    [InlineData(81.8, "high")]
    [InlineData(60.1, "high")]
    [InlineData(60.0, "medium")]
    [InlineData(30.0, "medium")]
    [InlineData(29.9, "low")]
    public void DependencyBands_FollowThePacksThresholds(double dependency, string band) =>
        Assert.Equal(band, new DependencyBands(60, 30).BandOf(dependency));

    // ── completeness: the worked example ────────────────────────────────

    private static readonly string[] Jobs = { "TLR_EOD_LOCK", "TLR_EOD_COUNT", "TLR_EOD_POST", "TLR_DW_EXTRACT", "TLR_EOD_REPORT", "TLR_AM_RECON" };
    private static readonly string[] Interfaces =
        { "loan-lookup", "gl-posting", "ofac-screening", "cash-vault-file", "drawer-limits", "teller-identity", "receipt-printer", "dw-extract" };
    private static readonly string[] Errors5 = { "KE-01", "KE-02", "KE-03", "KE-04", "KE-05" };

    private static Dictionary<string, IReadOnlyList<string>> Derived() => new()
    {
        ["batch-sheets"] = Jobs, ["interface-map"] = Interfaces, ["known-errors"] = Errors5,
    };

    private static readonly Dictionary<string, int> Expected = new() { ["jobs"] = 6, ["interfaces"] = 8, ["known-errors"] = 5 };

    /// <summary>A slot's credit as the worked example states it, as what was approved for it: 1 is all supported, 0.5 has one unverified statement, 0 is nothing approved.</summary>
    private static SlotEvidence Slot(string key, double credit) => new(key, credit switch
    {
        1.0 => new[] { new SlotVersion(true, new[] { StatementStatus.Supported, StatementStatus.Supported }) },
        0.5 => new[] { new SlotVersion(true, new[] { StatementStatus.Supported, StatementStatus.Unverified }) },
        _ => Array.Empty<SlotVersion>(),
    });

    // The "after a scripted review" credits, slot by slot.
    private static readonly Dictionary<string, double> After = new()
    {
        ["overview:purpose"] = 1, ["overview:users"] = 1, ["overview:business-process"] = 1, ["overview:tier"] = 1, ["overview:owners"] = 0.5,
        ["context:diagram"] = 1,
        ["entity:interface:loan-lookup"] = 1, ["entity:interface:gl-posting"] = 1, ["entity:interface:ofac-screening"] = 1, ["entity:interface:cash-vault-file"] = 1,
        ["entity:interface:drawer-limits"] = 1, ["entity:interface:teller-identity"] = 1, ["entity:interface:receipt-printer"] = 1, ["entity:interface:dw-extract"] = 0.5,
        ["entity:job:TLR_EOD_LOCK"] = 1, ["entity:job:TLR_EOD_COUNT"] = 1, ["entity:job:TLR_EOD_POST"] = 0.5, ["entity:job:TLR_DW_EXTRACT"] = 1,
        ["entity:job:TLR_EOD_REPORT"] = 1, ["entity:job:TLR_AM_RECON"] = 1,
        ["runbook:start-of-day"] = 1, ["runbook:end-of-day"] = 1, ["runbook:failure-handling"] = 0.5, ["runbook:restart"] = 1, ["runbook:escalation"] = 0,
        ["entity:error:KE-01"] = 1, ["entity:error:KE-02"] = 0.5, ["entity:error:KE-03"] = 0, ["entity:error:KE-04"] = 0.5, ["entity:error:KE-05"] = 1,
        ["env:environments"] = 1, ["env:access-groups"] = 1, ["env:contacts"] = 0,
        ["openq:closed"] = 0,
    };

    // The slots an older document already covered, all of it approved and supported.
    private static readonly string[] Legacy =
    {
        "overview:purpose", "overview:users", "entity:interface:loan-lookup", "entity:job:TLR_EOD_LOCK", "entity:job:TLR_AM_RECON",
        "runbook:start-of-day", "env:environments",
    };

    private static CompletenessResult Score(IEnumerable<SlotEvidence> slots, IReadOnlyDictionary<string, int>? expected = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? derived = null) =>
        Completeness.Compute(new CompletenessInput(Shipped(), derived ?? Derived(), expected ?? Expected, slots.ToList()));

    [Fact]
    public void Before_OnlyWhatAnOlderDocumentCoveredCounts_AndItIs18Point2Percent()
    {
        var result = Score(Legacy.Select(k => Slot(k, 1)));
        Assert.Equal(18.2, Math.Round(result.Percent, 1));
        Assert.Equal(81.8, Math.Round(result.DependencyPercent, 1));
        Assert.Equal("high", result.DependencyBand);

        var byId = result.Deliverables.ToDictionary(d => d.Id);
        Assert.Equal(2.0, byId["overview"].Earned);
        Assert.Equal(40.0, byId["overview"].Percent);
        Assert.Equal(1.0 / 8, byId["interface-map"].Earned / byId["interface-map"].Denominator);
        Assert.Equal(0.0, byId["context-diagram"].Percent);
    }

    [Fact]
    public void After_AScriptedReview_IsTheWorkedExample_76Point5Percent()
    {
        var result = Score(After.Select(kv => Slot(kv.Key, kv.Value)));
        Assert.Equal(76.5, Math.Round(result.Percent, 1));
        Assert.Equal(23.5, Math.Round(result.DependencyPercent, 1));
        Assert.Equal("low", result.DependencyBand);

        var byId = result.Deliverables.ToDictionary(d => d.Id);
        Assert.Equal(4.5, byId["overview"].Earned);
        Assert.Equal(7.5, byId["interface-map"].Earned);
        Assert.Equal(5.5, byId["batch-sheets"].Earned);
        Assert.Equal(3.5, byId["runbook"].Earned);
        Assert.Equal(3.0, byId["known-errors"].Earned);
        Assert.Equal(2.0, byId["environments-access-contacts"].Earned);
        Assert.Equal(0.0, byId["open-questions"].Earned);
        Assert.Equal(new[] { 5, 1, 8, 6, 5, 5, 3, 1 }, result.Deliverables.Select(d => d.Denominator).ToArray());
    }

    [Fact]
    public void ASlot_TakesItsBestCreditOverEveryApprovedVersion_SoApprovingLaterNeverLowersIt()
    {
        var best = Score(new[]
        {
            new SlotEvidence("runbook:restart", new[]
            {
                new SlotVersion(true, new[] { StatementStatus.Supported, StatementStatus.Unverified }),   // 0.5
                new SlotVersion(true, new[] { StatementStatus.Supported }),                                // 1.0
                new SlotVersion(false, new[] { StatementStatus.Supported }),                               // a later draft, not approved
                new SlotVersion(true, new[] { StatementStatus.Conflict }),                                 // a later one with a conflict open
            }),
        });
        var slot = best.Deliverables.Single(d => d.Id == "runbook").Slots.Single(s => s.SlotKey == "runbook:restart");
        Assert.Equal(1.0, slot.Credit);
        Assert.Equal(SlotState.ApprovedSupported, slot.State);

        var onlyUnapprovedOrConflicted = Score(new[]
        {
            new SlotEvidence("runbook:restart", new[] { new SlotVersion(false, new[] { StatementStatus.Supported }), new SlotVersion(true, new[] { StatementStatus.Conflict }) }),
        }).Deliverables.Single(d => d.Id == "runbook").Slots.Single(s => s.SlotKey == "runbook:restart");
        Assert.Equal(0.0, onlyUnapprovedOrConflicted.Credit);
        Assert.Equal(SlotState.NotApproved, onlyUnapprovedOrConflicted.State);
    }

    [Fact]
    public void ThingsExpectedButNotFound_CountAgainstTheDeliverable_AndAreShown()
    {
        // Eight interfaces expected, seven found and all seven fully approved.
        var seven = Interfaces.Take(7).ToList();
        var result = Score(seven.Select(i => Slot("entity:interface:" + i, 1)),
            derived: new Dictionary<string, IReadOnlyList<string>> { ["interface-map"] = seven });

        var map = result.Deliverables.Single(d => d.Id == "interface-map");
        Assert.Equal(8, map.Denominator);
        Assert.Equal(7.0, map.Earned);
        var missing = Assert.Single(map.Slots, s => s.State == SlotState.Missing);
        Assert.Contains("not found", missing.SlotKey);
    }

    [Fact]
    public void MoreFoundThanExpected_CannotEarnMoreThanTheExpectedCount()
    {
        var ten = Enumerable.Range(1, 10).Select(i => $"if-{i:00}").ToList();
        var result = Score(ten.Select(i => Slot("entity:interface:" + i, 1)),
            expected: new Dictionary<string, int> { ["interfaces"] = 8 },
            derived: new Dictionary<string, IReadOnlyList<string>> { ["interface-map"] = ten });

        var map = result.Deliverables.Single(d => d.Id == "interface-map");
        Assert.Equal(8, map.Denominator);
        Assert.Equal(8.0, map.Earned);       // not 10
        Assert.Equal(100.0, map.Percent);
    }

    [Fact]
    public void WithNoExpectedCount_TheThingsFoundAreTheDenominator()
    {
        var result = Score(new[] { Slot("entity:job:TLR_EOD_POST", 1) },
            expected: new Dictionary<string, int>(),
            derived: new Dictionary<string, IReadOnlyList<string>> { ["batch-sheets"] = new[] { "TLR_EOD_POST", "TLR_EOD_LOCK" } });
        var jobs = result.Deliverables.Single(d => d.Id == "batch-sheets");
        Assert.Equal(2, jobs.Denominator);
        Assert.Equal(50.0, jobs.Percent);
    }

    [Fact]
    public void ADeliverableWithNothingToFill_TakesNoPartInTheScore()
    {
        // No jobs, interfaces or errors are found and none are expected: those three have no slots to fill.
        var result = Score(
            new[] { "overview:purpose", "overview:users", "overview:business-process", "overview:tier", "overview:owners" }.Select(k => Slot(k, 1)),
            expected: new Dictionary<string, int>(),
            derived: new Dictionary<string, IReadOnlyList<string>>());

        Assert.All(new[] { "batch-sheets", "interface-map", "known-errors" }, id =>
        {
            var d = result.Deliverables.Single(x => x.Id == id);
            Assert.False(d.Applicable);
            Assert.Empty(d.Slots);
        });
        // Overview is the only deliverable finished; out of the weights that apply (100 − 15 − 15 − 15 = 55), it is 10 of 55.
        Assert.Equal(10.0 / 55 * 100, result.Percent, 6);
    }

    [Fact]
    public void NothingApproved_IsZeroPercent_AndEverythingDependsOnThePeopleWhoKnow()
    {
        var result = Score(Array.Empty<SlotEvidence>());
        Assert.Equal(0, result.Percent);
        Assert.Equal(100, result.DependencyPercent);
        Assert.Equal("high", result.DependencyBand);
    }
}
