using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// A docs stage that fails says why. Before, the reason went to the server log only and the Docs page showed
/// "Generation failed." for a whole run (found on the Requirements pack of a project whose four stages all failed).
/// </summary>
public class CatalogFailureTests
{
    [Fact]
    public void A_stage_outcome_without_an_error_is_a_success_as_before()
    {
        var ok = new CatalogPipeline.StageOutcome(12, 0);
        Assert.Null(ok.Error);
        Assert.Equal(("x", "y"), (new CatalogPipeline.StageOutcome(0, 1, "x").Error, "y"));
    }

    [Fact]
    public void A_provider_error_keeps_the_providers_own_reason()
    {
        var ex = new InvalidOperationException("Anthropic returned 429 Too Many Requests: {\"type\":\"error\",\n  \"error\":{\"type\":\"rate_limit_error\"}}");
        var text = CatalogPipeline.DescribeFailure(ex);
        Assert.StartsWith("InvalidOperationException: Anthropic returned 429", text);
        Assert.Contains("rate_limit_error", text);
        Assert.DoesNotContain('\n', text);                                   // one line for the log and the page
    }

    [Fact]
    public void A_cancelled_stage_is_called_interrupted_not_failed_for_no_reason()
    {
        Assert.Contains("interrupted", CatalogPipeline.DescribeFailure(new TaskCanceledException()));
        Assert.Contains("restarted", CatalogPipeline.DescribeFailure(new OperationCanceledException()));
    }

    [Fact]
    public void The_cause_beneath_is_added_when_it_says_more_and_long_text_is_cut()
    {
        var ex = new InvalidOperationException("The stage failed.", new HttpRequestException("Name or service not known"));
        Assert.Contains("(HttpRequestException: Name or service not known)", CatalogPipeline.DescribeFailure(ex));

        var long500 = CatalogPipeline.DescribeFailure(new InvalidOperationException(new string('x', 2000)));
        Assert.Equal(501, long500.Length);
        Assert.EndsWith("…", long500);
    }
}

public class SectionTitleTests
{
    private static string? T(string json, string? md = null) =>
        Astra.Api.Endpoints.DocsEndpoints.SectionTitle(System.Text.Json.JsonDocument.Parse(json), md);

    [Fact]
    public void A_requirement_is_named_by_its_reference_and_statement() =>
        Assert.Equal("FR-003 — Rates come from the hidden sheet", T("{\"reference\":\"FR-003\",\"statement\":\"Rates come from the hidden sheet\"}"));

    [Fact]
    public void A_capability_or_flow_uses_its_name_and_a_diagram_falls_back_to_its_heading()
    {
        Assert.Equal("Quote a policy", T("{\"name\":\"Quote a policy\"}"));
        Assert.Equal("Call graph", T("{}", "\n## Call graph\n\nx"));
        Assert.Null(T("{}", null));
    }

    [Fact]
    public void Long_titles_are_cut_to_one_line()
    {
        var t = T("{\"name\":\"" + new string('a', 400) + "\"}")!;
        Assert.Equal(160, t.Length);
        Assert.EndsWith("…", t);
    }
}