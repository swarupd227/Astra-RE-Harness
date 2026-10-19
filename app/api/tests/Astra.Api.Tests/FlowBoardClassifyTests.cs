using Astra.Api.Copilot;
using Astra.Api.Endpoints;
using Xunit;

namespace Astra.Api.Tests;

public class FlowBoardClassifyTests
{
    private static readonly string[] AllGates = { "COMPILE", "TEST_PACK", "EQUIVALENCE", "FALSIFYING" };

    private static Dictionary<string, string> Gates(params (string Stage, string Status)[] runs) =>
        runs.ToDictionary(r => r.Stage, r => r.Status);

    private static Dictionary<string, string> AllPassed() => AllGates.ToDictionary(g => g, _ => "PASSED");

    [Theory]
    [InlineData("PARSED", "parsed")]
    [InlineData("EXTRACTING", "extracting")]
    // A state the board doesn't know about is "not started" rather than a crash.
    [InlineData("SOMETHING_NEW", "parsed")]
    public void No_spec_follows_the_subroutine_state(string subState, string expected) =>
        Assert.Equal(expected, FlowBoardEndpoints.ClassifyColumn(subState, null, null, null));

    [Fact]
    public void Spec_state_wins_over_a_stale_subroutine_flag()
    {
        Assert.Equal("draft", FlowBoardEndpoints.ClassifyColumn("PARSED", "DRAFT", null, null));
        Assert.Equal("in_review", FlowBoardEndpoints.ClassifyColumn("DRAFT", "IN_REVIEW", null, null));
    }

    [Theory]
    [InlineData("SIGNED")]
    // Generation in flight: the scaffold row only exists once it succeeds.
    [InlineData("SCAFFOLDING")]
    public void Signed_spec_without_a_scaffold_is_signed(string subState) =>
        Assert.Equal("signed", FlowBoardEndpoints.ClassifyColumn(subState, "SIGNED", null, null));

    [Fact]
    public void Scaffold_with_no_gate_runs_is_built() =>
        Assert.Equal("built", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "SCAFFOLDED", null));

    [Fact]
    public void Verified_needs_all_four_gates_passed()
    {
        Assert.Equal("verified", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "SCAFFOLDED", AllPassed()));
    }

    [Fact]
    public void Three_of_four_gates_passed_is_still_built()
    {
        var gates = Gates(("COMPILE", "PASSED"), ("TEST_PACK", "PASSED"), ("EQUIVALENCE", "PASSED"));
        Assert.Equal("built", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "SCAFFOLDED", gates));
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("ERRORED")]
    [InlineData("RUNNING")]
    public void A_gate_that_is_not_passed_keeps_it_built(string status)
    {
        var gates = AllPassed();
        gates["EQUIVALENCE"] = status;
        Assert.Equal("built", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "SCAFFOLDED", gates));
    }

    [Fact]
    public void Failed_scaffold_stays_built()
    {
        Assert.Equal("built", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "FAILED", Gates(("COMPILE", "FAILED"))));
    }

    [Fact]
    public void Committed_scaffold_is_committed_whatever_the_gates_say()
    {
        Assert.Equal("committed", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "COMMITTED", null));
        Assert.Equal("committed", FlowBoardEndpoints.ClassifyColumn("SCAFFOLDED", "SIGNED", "COMMITTED", AllPassed()));
    }

    [Fact]
    public void Every_classification_lands_in_a_declared_column()
    {
        var states = new[] { (string?)null, "DRAFT", "IN_REVIEW", "SIGNED" };
        var scaffolds = new[] { (string?)null, "SCAFFOLDED", "COMMITTED", "FAILED" };
        foreach (var sub in new[] { "PARSED", "EXTRACTING", "SIGNED", "SCAFFOLDED" })
        foreach (var spec in states)
        foreach (var scaffold in scaffolds)
            Assert.Contains(FlowBoardEndpoints.ClassifyColumn(sub, spec, scaffold, null), FlowBoardEndpoints.ColumnKeys);
    }

    [Fact]
    public void GateMap_lists_all_four_gates_in_order_and_says_NOT_RUN_for_gates_that_never_ran()
    {
        var map = FlowBoardEndpoints.GateMap(Gates(("COMPILE", "PASSED"), ("TEST_PACK", "PASSED")));
        Assert.Equal(new[] { "COMPILE", "TEST_PACK", "EQUIVALENCE", "FALSIFYING" }, map.Keys);
        Assert.Equal("PASSED", map["COMPILE"]);
        Assert.Equal("PASSED", map["TEST_PACK"]);
        Assert.Equal("NOT_RUN", map["EQUIVALENCE"]);
        Assert.Equal("NOT_RUN", map["FALSIFYING"]);
    }

    [Fact]
    public void GateMap_with_no_runs_at_all_is_all_NOT_RUN() =>
        Assert.All(FlowBoardEndpoints.GateMap(null).Values, v => Assert.Equal("NOT_RUN", v));

    [Fact]
    public void Tally_counts_every_routine_per_gate_per_result()
    {
        var byStage = new Dictionary<string, Dictionary<string, int>>();
        FlowBoardEndpoints.Tally(byStage, FlowBoardEndpoints.GateMap(Gates(("COMPILE", "PASSED"), ("TEST_PACK", "PASSED"))));
        FlowBoardEndpoints.Tally(byStage, FlowBoardEndpoints.GateMap(Gates(("COMPILE", "PASSED"), ("TEST_PACK", "FAILED"))));
        FlowBoardEndpoints.Tally(byStage, FlowBoardEndpoints.GateMap(Gates(("COMPILE", "FAILED"))));

        Assert.Equal(2, byStage["COMPILE"]["PASSED"]);
        Assert.Equal(1, byStage["COMPILE"]["FAILED"]);
        Assert.Equal(1, byStage["TEST_PACK"]["PASSED"]);
        Assert.Equal(1, byStage["TEST_PACK"]["FAILED"]);
        Assert.Equal(1, byStage["TEST_PACK"]["NOT_RUN"]);
        Assert.Equal(3, byStage["EQUIVALENCE"]["NOT_RUN"]);
        // Every gate's results account for every routine tallied.
        Assert.All(byStage.Values, counts => Assert.Equal(3, counts.Values.Sum()));
    }

    [Fact]
    public void Model_payload_states_gate_results_for_built_routines_and_the_stage_tally()
    {
        var gates = FlowBoardEndpoints.GateMap(Gates(("COMPILE", "PASSED"), ("TEST_PACK", "PASSED")));
        var built = new FlowBoardRoutine(Guid.NewGuid(), "takeAction", "src/a.cpp", 1, 2, "SCAFFOLDED", gates);
        var parsed = new FlowBoardRoutine(Guid.NewGuid(), "Foo", "src/b.cpp", 1, 2, null);
        var tally = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["COMPILE"] = new Dictionary<string, int> { ["PASSED"] = 1 },
            ["TEST_PACK"] = new Dictionary<string, int> { ["PASSED"] = 1 },
            ["EQUIVALENCE"] = new Dictionary<string, int> { ["NOT_RUN"] = 1 },
            ["FALSIFYING"] = new Dictionary<string, int> { ["NOT_RUN"] = 1 },
        };
        var columns = FlowBoardEndpoints.ColumnKeys.Select(k => k switch
        {
            "built" => new FlowBoardColumn(k, "Built", 1, new[] { built }, false, tally),
            "parsed" => new FlowBoardColumn(k, "Parsed", 1, new[] { parsed }, false),
            _ => new FlowBoardColumn(k, FlowBoardEndpoints.Label(k), 0, Array.Empty<FlowBoardRoutine>(), false),
        }).ToList();

        var (_, payload) = ArtifactBuilders.FlowBoard(new FlowBoardResult(Guid.NewGuid(), "oatpp", 2, columns));
        var model = System.Text.Json.JsonSerializer.SerializeToElement(payload);
        var stages = model.GetProperty("stages").EnumerateArray().ToDictionary(s => s.GetProperty("stage").GetString()!);

        var ex = stages["built"].GetProperty("examples")[0];
        Assert.Equal("takeAction", ex.GetProperty("routine").GetString());
        Assert.Equal("PASSED", ex.GetProperty("gates").GetProperty("TEST_PACK").GetString());
        Assert.Equal("NOT_RUN", ex.GetProperty("gates").GetProperty("EQUIVALENCE").GetString());
        Assert.Equal(1, stages["built"].GetProperty("gateTally").GetProperty("COMPILE").GetProperty("PASSED").GetInt32());

        // Routines without code have no gates to report, and a stage without code has no tally.
        Assert.False(stages["parsed"].GetProperty("examples")[0].TryGetProperty("gates", out _));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, stages["parsed"].GetProperty("gateTally").ValueKind);
    }

    [Fact]
    public void Card_props_and_model_payload_carry_the_same_counts_and_the_model_sees_few_names()
    {
        var routines = Enumerable.Range(1, 12)
            .Select(i => new FlowBoardRoutine(Guid.NewGuid(), $"R{i}", "src/a.f", i, i, null))
            .ToList();
        var columns = FlowBoardEndpoints.ColumnKeys
            .Select(k => k == "draft"
                ? new FlowBoardColumn(k, FlowBoardEndpoints.Label(k), 40, routines, true)
                : new FlowBoardColumn(k, FlowBoardEndpoints.Label(k), 0, Array.Empty<FlowBoardRoutine>(), false))
            .ToList();
        var board = new FlowBoardResult(Guid.NewGuid(), "Demo", 40, columns);

        var (artifact, payload) = ArtifactBuilders.FlowBoard(board);

        Assert.Equal("flowBoard", artifact.Kind);
        Assert.Equal(board.CorpusId.ToString(), artifact.RefId);
        var model = System.Text.Json.JsonSerializer.SerializeToElement(payload);
        var draft = model.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetString() == "draft");
        Assert.Equal(40, draft.GetProperty("count").GetInt32());
        // The model gets counts and a handful of names, not the card's 12.
        Assert.Equal(5, draft.GetProperty("examples").GetArrayLength());

        var cardDraft = artifact.Props.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "draft");
        Assert.Equal(40, cardDraft.GetProperty("total").GetInt32());
        Assert.Equal(12, cardDraft.GetProperty("routines").GetArrayLength());
        Assert.True(cardDraft.GetProperty("hasMore").GetBoolean());
    }
}
