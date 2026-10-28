using Astra.Api.Copilot;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The server-side half of "every fact comes from a tool called this turn".
/// On the deployed build the model answered "what's built but not verified?"
/// with no tool at all, copying the previous turn's result: right that time,
/// stale the moment anything changed. The orchestrator now holds back a
/// tool-less finish once and gives the model a chance to call the tool.
/// </summary>
public class GroundingCheckTests
{
    private const string Finish = CopilotOrchestrator.FinishTool;

    [Fact]
    public void A_bare_finish_with_no_tool_called_is_held_back_once()
    {
        Assert.True(CopilotOrchestrator.NeedsGroundingCheck(false, 0, new[] { Finish }));
    }

    [Fact]
    public void A_plain_text_answer_with_no_tools_in_the_round_is_held_back_too()
    {
        Assert.True(CopilotOrchestrator.NeedsGroundingCheck(false, 0, Array.Empty<string?>()));
    }

    [Fact]
    public void The_check_is_spent_after_one_use_so_the_same_answer_then_goes_through()
    {
        Assert.False(CopilotOrchestrator.NeedsGroundingCheck(true, 0, new[] { Finish }));
    }

    [Fact]
    public void Once_a_tool_has_been_called_this_turn_the_answer_is_not_held_back()
    {
        // Covers a turn where earlier rounds called get_flow_board and this one finishes,
        // and a confirmed action's follow-up reply (the paused call is carried into the turn).
        Assert.False(CopilotOrchestrator.NeedsGroundingCheck(false, 1, new[] { Finish }));
    }

    [Fact]
    public void A_round_that_calls_a_real_tool_next_to_finish_is_not_held_back()
    {
        Assert.False(CopilotOrchestrator.NeedsGroundingCheck(false, 0, new[] { "get_flow_board", Finish }));
        Assert.False(CopilotOrchestrator.NeedsGroundingCheck(false, 0, new[] { Finish, "get_flow_board" }));
    }

    [Fact]
    public void The_message_to_the_model_names_both_ways_out()
    {
        var m = CopilotOrchestrator.GroundingCheckMessage;
        // Call the tool, or say the same thing again — a turn with nothing to look up must not be stuck.
        Assert.Contains("call the relevant read tool", m);
        Assert.Contains("get_flow_board", m);
        Assert.Contains("call finish_turn again with the same answer", m);
        Assert.Contains("once per turn", m);
    }
}
