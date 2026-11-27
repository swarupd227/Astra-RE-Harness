using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

public class ModelUseTests
{
    [Theory]
    [InlineData("copilot-orchestrator", "Conversation (agents)")]
    [InlineData("fortran-extract", "Spec extraction")]
    [InlineData("vb6/dotnet10/extract", "Spec extraction")]
    [InlineData("dotnet-scaffold", "Code generation")]
    [InlineData("delphi/dotnet10-faithful/faithful-transform", "Code generation")]
    [InlineData("survey-digest", "Pattern analysis")]
    [InlineData("docs/routine", "Documents")]
    [InlineData("quality-review", "Code review")]
    [InlineData("assessment", "Assessment")]
    [InlineData("knowledge/statements", "Knowledge capture")]
    [InlineData("", "Other")]
    [InlineData(null, "Other")]
    public void Names_the_job_a_call_served(string? promptId, string expected) =>
        Assert.Equal(expected, ModelUse.PurposeOf(promptId));
}
