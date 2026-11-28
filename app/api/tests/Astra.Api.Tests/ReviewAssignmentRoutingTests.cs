using System.Text.Json;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Review assignment is demonstrable as a sentence: the mock brain routes it to the assign_review tool.</summary>
public class ReviewAssignmentRoutingTests
{
    [Theory]
    [InlineData("Assign `getBooks` to Priya", "getBooks", "Priya")]
    [InlineData("assign `CONSUME_ROLL` to @johon", "CONSUME_ROLL", "johon")]
    [InlineData("Please assign the review of `HYBRD1` to Maria Lopez", "HYBRD1", "Maria Lopez")]
    [InlineData("unassign `getBooks`", "getBooks", "")]
    public void The_mock_brain_routes_assignment_requests(string text, string unit, string reviewer)
    {
        var (name, input) = Astra.Api.Copilot.MockCopilotBrain.RouteOrNull(text)!.Value;
        Assert.Equal("assign_review", name);
        var json = JsonSerializer.SerializeToElement(input);
        Assert.Equal(unit, json.GetProperty("subroutineId").GetString());
        Assert.Equal(reviewer, json.GetProperty("reviewer").GetString());
    }

    [Fact]
    public void Accept_still_routes_to_accepting_claims()
    {
        var (name, _) = Astra.Api.Copilot.MockCopilotBrain.RouteOrNull("accept everything in `getBooks`")!.Value;
        Assert.Equal("review_all_claims", name);
    }
}
