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
