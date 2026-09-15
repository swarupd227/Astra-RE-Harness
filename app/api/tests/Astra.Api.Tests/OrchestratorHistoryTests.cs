using System.Text.Json;
using Astra.Api.Copilot;
using Astra.Api.Persistence.Entities;
using Xunit;

namespace Astra.Api.Tests;

public class OrchestratorHistoryTests
{
    private static ConversationMessage Msg(string role, string? agent, string markdown, string toolCalls = "[]", string? pending = null, int minute = 0) => new()
    {
        Id = Guid.NewGuid(),
        Role = role,
        Agent = agent,
        Markdown = markdown,
        ToolCallsJson = toolCalls,
        PendingActionJson = pending,
        CreatedAt = new DateTimeOffset(2026, 9, 15, 3, minute, 0, TimeSpan.Zero),
    };

    private static JsonElement Ser(object turn) => JsonSerializer.SerializeToElement(turn);

    [Fact]
    public void Earlier_actions_appear_as_tool_calls_with_results_not_as_prose()
    {
        var rows = new[]
        {
            Msg("user", null, "Convert `TBlogApplication.ArticleView` 1:1 to .NET 10", minute: 0),
            Msg("agent", "orchestrator",
                "I'll generate dotnet10-faithful code for `TBlogApplication.ArticleView`.\n\nConfirm and I'll go ahead.",
                toolCalls: """[{"name":"get_spec","input":{"subroutineId":"TBlogApplication.ArticleView"},"summary":"21 claims, SIGNED","ok":true,"durationMs":40}]""",
                pending: """{"toolName":"generate_scaffold","input":{"subroutineId":"TBlogApplication.ArticleView","targetStack":"dotnet10-faithful"},"summary":"generate dotnet10-faithful code","requiredPersona":"engineer","state":"confirmed"}""",
                minute: 1),
            Msg("agent", "migration", "Converted `MVCViewModel.pas` 1:1 to .NET 10 (16 routines).", minute: 2),
            Msg("user", null, "Run the compile gate", minute: 3),
        };

        var history = CopilotOrchestrator.BuildHistory(rows).Select(Ser).ToList();

        Assert.Equal(new[] { "user", "assistant", "user" }, history.Select(h => h.GetProperty("role").GetString()));

        var assistant = history[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("text", assistant[0].GetProperty("type").GetString());
        Assert.DoesNotContain("Confirm and I'll go ahead", assistant[0].GetProperty("text").GetString());
        Assert.Equal(new[] { "tool_use", "tool_use" }, assistant.Skip(1).Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("get_spec", assistant[1].GetProperty("name").GetString());
        Assert.Equal("generate_scaffold", assistant[2].GetProperty("name").GetString());
        Assert.Equal("dotnet10-faithful", assistant[2].GetProperty("input").GetProperty("targetStack").GetString());

        // Results come first in the following user turn, one per tool_use, ids matching;
        // the Migration agent's post and the next user sentence follow as text.
        var user = history[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(new[] { "tool_result", "tool_result", "text", "text" }, user.Select(b => b.GetProperty("type").GetString()));
        Assert.Equal(assistant[1].GetProperty("id").GetString(), user[0].GetProperty("tool_use_id").GetString());
        Assert.Equal(assistant[2].GetProperty("id").GetString(), user[1].GetProperty("tool_use_id").GetString());
        Assert.Contains("confirmed", user[1].GetProperty("content").GetString());
        Assert.False(user[1].TryGetProperty("is_error", out _));
        Assert.StartsWith("[Migration agent posted to the thread]", user[2].GetProperty("text").GetString());
        Assert.Equal("Run the compile gate", user[3].GetProperty("text").GetString());
    }

    [Fact]
    public void A_declined_action_is_an_error_result_and_history_starts_with_the_user()
    {
        var rows = new[]
        {
            Msg("agent", "orchestrator", "Welcome to the programme.", minute: 0),
            Msg("user", null, "Sign it", minute: 1),
            Msg("agent", "orchestrator", "I'll sign the spec.",
                pending: """{"toolName":"sign_spec","input":{"subroutineId":"X"},"summary":"sign X","requiredPersona":"sme","state":"declined"}""",
                minute: 2),
        };

        var history = CopilotOrchestrator.BuildHistory(rows).Select(Ser).ToList();

        Assert.Equal("user", history[0].GetProperty("role").GetString());
        var result = history[2].GetProperty("content").EnumerateArray().First();
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.Contains("declined", result.GetProperty("content").GetString());
    }

    [Fact]
    public void A_window_that_opens_on_an_orchestrator_turn_leaves_no_orphaned_result()
    {
        // Exactly the shape that returned 400 on Azure: the history window began
        // with the pause message of an earlier confirmed action.
        var rows = new[]
        {
            Msg("agent", "orchestrator", "I'll regenerate it.",
                pending: """{"toolName":"generate_scaffold","input":{"subroutineId":"X"},"summary":"regenerate X","requiredPersona":"engineer","state":"confirmed"}""",
                minute: 0),
            Msg("agent", "orchestrator", "Regenerating now.", minute: 1),
            Msg("agent", "migration", "Converted `X.pas` 1:1 to .NET 10.", minute: 2),
            Msg("user", null, "Run the compile gate", minute: 3),
        };

        var history = CopilotOrchestrator.BuildHistory(rows).Select(Ser).ToList();

        Assert.Equal("user", history[0].GetProperty("role").GetString());
        var first = history[0].GetProperty("content").EnumerateArray().ToList();
        Assert.DoesNotContain(first, b => b.GetProperty("type").GetString() == "tool_result");
        Assert.Contains("[Migration agent posted to the thread]", first[0].GetProperty("text").GetString());
        Assert.Equal("Run the compile gate", first[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("I'll regenerate the conversion now. Confirm and I'll go ahead.", true)]
    [InlineData("Retrying. [proposed action run_gate: confirmed]", true)]
    [InlineData("The spec has 21 claims; the riskiest is INV-3.", false)]
    public void A_narrated_action_without_a_tool_call_is_recognised(string text, bool expected) =>
        Assert.Equal(expected, CopilotOrchestrator.LooksLikeUncalledAction(new[] { text }));
}
