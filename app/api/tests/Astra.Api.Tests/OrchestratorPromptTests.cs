using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// Guards the one rule that makes the orchestrator trustworthy: facts come
/// from a tool called in the same turn. v1.1 let the model also repeat what
/// "a tool posted to the thread earlier" — and on a long thread it repeated
/// a claim that was never true (a spec "carried forward") and invented a gate
/// result (TEST_PACK "hasn't run") without calling any tool. A prompt edit
/// that quietly restores the loophole should fail here, not on Azure.
/// </summary>
public class OrchestratorPromptTests
{
    private static string PromptText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Astra.Api", "Llm", "Prompts")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Astra.Api", "Llm", "Prompts", "common", "dotnet8", "copilot-orchestrator.v1.md"));
    }

    [Fact]
    public void Facts_must_come_from_a_tool_called_this_turn_not_from_the_thread()
    {
        var p = PromptText();
        Assert.Contains("a tool you called this turn", p);
        Assert.Contains("The thread above is history, not evidence", p);
        Assert.DoesNotContain("tool posted to the thread earlier", p);
    }

    [Fact]
    public void Where_something_came_from_is_a_fact_too()
    {
        // The false "carried forward during re-sync" claim was provenance.
        Assert.Contains("where something came from", PromptText());
    }

    [Fact]
    public void Pipeline_state_questions_route_to_the_flow_board_every_time()
    {
        var p = PromptText();
        Assert.Contains("`get_flow_board`", p);
        Assert.Contains("every time you are asked", p);
    }
}
