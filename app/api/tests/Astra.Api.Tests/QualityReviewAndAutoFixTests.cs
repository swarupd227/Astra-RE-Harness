using System.Text.Json;
using Astra.Api.Copilot;
using Astra.Api.Persistence.Entities;
using Astra.Api.Validation;
using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// Q2 of the quality gate: the reviewer agent's findings survive only when the code they quote is really there, its
/// findings never decide the verdict, and the fix-until-green loop judges each round consistently.
/// </summary>
public class QualityReviewAndAutoFixTests
{
    private static readonly (string Path, string Content)[] Package =
    [
        ("backend/app/service.py", "import logging\nlog = logging.getLogger(__name__)\n\ndef quote(req):\n    log.info(\"quote for %s\", req.email)\n    premium = round(req.base * req.factor)\n    return premium\n"),
        ("backend/app/api.py", "@router.post(\"/api/premium\")\ndef calc(body: QuoteIn):\n    return service.quote(body)\n"),
    ];

    private static QualityReviewVerifier.Proposal P(string file, int? line, string quote, string severity = "major", string category = "security", string? claim = null) =>
        new(file, line, severity, category, "Personal data is logged", quote, "Emails end up in logs.", "Log an id instead.", claim);

    [Fact]
    public void A_finding_whose_quote_is_in_the_file_is_kept_at_the_line_where_the_quote_is()
    {
        var (kept, dropped) = QualityReviewVerifier.Verify(
            [P("backend/app/service.py", 9, "log.info(\"quote for %s\", req.email)")], Package, new HashSet<string>());
        Assert.Equal(0, dropped);
        var f = Assert.Single(kept);
        Assert.Equal(5, f.Line);                       // the line comes from the code, not from the model's guess
        Assert.Equal("ai", f.Source);
        Assert.Equal("security", f.Category);
        Assert.Equal("AI-SECURITY", f.Rule);
    }

    [Theory]
    [InlineData("backend/app/service.py", "log.info(\"quote for %s\", req.phone)")]   // not in the file
    [InlineData("backend/app/missing.py", "log.info(\"quote for %s\", req.email)")]   // no such file
    [InlineData("backend/app/service.py", "x")]                                        // too short to prove anything
    public void A_finding_that_does_not_point_at_real_code_is_dropped(string file, string quote)
    {
        var (kept, dropped) = QualityReviewVerifier.Verify([P(file, 5, quote)], Package, new HashSet<string>());
        Assert.Empty(kept);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void A_quote_copied_with_line_numbers_or_other_spacing_still_matches_and_a_short_path_resolves()
    {
        var quote = "6|     premium = round(req.base  *  req.factor)\n7|     return premium";
        var (kept, _) = QualityReviewVerifier.Verify([P("app/service.py", null, quote, "critical", "correctness")], Package, new HashSet<string>());
        var f = Assert.Single(kept);
        Assert.Equal("backend/app/service.py", f.File);
        Assert.Equal(6, f.Line);
        Assert.Equal("review", f.Category);
    }

    [Fact]
    public void A_claim_id_the_signed_spec_does_not_have_is_removed()
    {
        var ids = QualityReviewVerifier.ClaimIds("""{"invariants":[{"id":"INV-1","text":"a"}],"edge_cases":[{"id":"EC-2"}]}""");
        Assert.Contains("INV-1", ids);
        Assert.Contains("EC-2", ids);
        var (kept, _) = QualityReviewVerifier.Verify(
            [P("backend/app/service.py", 6, "premium = round(req.base * req.factor)", claim: "INV-1"),
             P("backend/app/api.py", 2, "def calc(body: QuoteIn):", claim: "INV-99")], Package, ids);
        Assert.Equal("INV-1", kept[0].SpecClaimId);
        Assert.Contains("(claim INV-1)", kept[0].Message);
        Assert.Null(kept[1].SpecClaimId);
    }

    [Fact]
    public void The_reviewer_s_findings_are_listed_but_never_fail_the_gate()
    {
        var ai = new QualityFinding("AI-SECURITY", "security", "critical", "backend/app/service.py", 5, "m", "w", "f", Source: "ai");
        var summary = QualityRules.Summarise([ai]);
        Assert.True(summary.Passed);
        Assert.Equal(0, summary.Blocking);
        Assert.Equal(1, summary.BySeverity["critical"]);
    }

    [Fact]
    public void The_proposals_are_read_from_the_tool_call_and_bad_json_gives_none()
    {
        var json = """{"findings":[{"file":"a.py","line":3,"severity":"minor","category":"design","title":"t","quote":"q","why":"w","fix":"f"}]}""";
        var p = Assert.Single(QualityReviewVerifier.ParseProposals(json));
        Assert.Equal(3, p.Line);
        Assert.Empty(QualityReviewVerifier.ParseProposals("not json"));
    }

    [Fact]
    public void The_package_is_shown_to_the_reviewer_numbered_main_code_first_within_its_budget()
    {
        var files = new List<(string, string)> { ("backend/tests/test_x.py", "def test_a():\n    pass"), ("backend/app/x.py", "a = 1\nb = 2") };
        var text = QualityReviewer.RenderFiles(files, 10_000);
        Assert.True(text.IndexOf("backend/app/x.py", StringComparison.Ordinal) < text.IndexOf("backend/tests/test_x.py", StringComparison.Ordinal));
        Assert.Contains("2| b = 2", text);
        var tight = QualityReviewer.RenderFiles(files, 40);
        Assert.Contains("Not shown for size", tight);
    }

    private static ValidationRun Run(string stage, string status, object? metrics = null) => new()
    {
        Id = Guid.NewGuid(), Stage = stage, Status = status, Summary = status.ToLowerInvariant(),
        MetricsJson = metrics is null ? null : JsonSerializer.Serialize(metrics), StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void A_round_is_green_only_when_compile_tests_and_quality_all_pass()
    {
        var green = new AutoFixService.RoundResult([Run("COMPILE", "PASSED"), Run("TEST_PACK", "PASSED", new { passed = 40, failed = 0 }), Run("QUALITY", "PASSED")]);
        Assert.True(green.Green);
        var noQuality = new AutoFixService.RoundResult([Run("COMPILE", "PASSED"), Run("TEST_PACK", "PASSED")]);
        Assert.False(noQuality.Green);
        var red = new AutoFixService.RoundResult([Run("COMPILE", "PASSED"), Run("TEST_PACK", "FAILED", new { passed = 37, failed = 2 }), Run("QUALITY", "PASSED")]);
        Assert.False(red.Green);
        Assert.Single(red.Failed);
        Assert.False(red.RunnerProblem);
    }

    [Fact]
    public void Rounds_are_ranked_so_a_worse_round_can_be_rolled_back()
    {
        var noCompile = new AutoFixService.RoundResult([Run("COMPILE", "FAILED"), Run("QUALITY", "PASSED")]);
        var twoFailing = new AutoFixService.RoundResult([Run("COMPILE", "PASSED"), Run("TEST_PACK", "FAILED", new { passed = 37, failed = 2 }), Run("QUALITY", "PASSED", new { blocking = 0 })]);
        var oneFailing = new AutoFixService.RoundResult([Run("COMPILE", "PASSED"), Run("TEST_PACK", "FAILED", new { passed = 38, failed = 1 }), Run("QUALITY", "PASSED", new { blocking = 0 })]);
        var testsGreenQualityRed = new AutoFixService.RoundResult([Run("COMPILE", "PASSED"), Run("TEST_PACK", "PASSED", new { passed = 39, failed = 0 }), Run("QUALITY", "FAILED", new { blocking = 2 })]);
        Assert.True(twoFailing.Score > noCompile.Score);
        Assert.True(oneFailing.Score > twoFailing.Score);
        Assert.True(testsGreenQualityRed.Score > oneFailing.Score);   // passing the claim tests matters more than style
        Assert.True(new AutoFixService.RoundResult([Run("COMPILE", "ERRORED")]).RunnerProblem);
    }

    [Theory]
    [InlineData("fix `CalculateApiPremium` until it's green", "fix_until_green", null)]
    [InlineData("auto-fix PostJson", "fix_until_green", null)]
    [InlineData("run quality checks on `PostJson`", "run_gate", "quality")]
    [InlineData("security review of PostJson", "run_gate", "quality")]
    [InlineData("run the compile gate on PostJson", "run_gate", "compile")]
    public void The_thread_commands_reach_the_right_tool(string text, string tool, string? gate)
    {
        var routed = MockCopilotBrain.RouteOrNull(text);
        Assert.NotNull(routed);
        Assert.Equal(tool, routed!.Value.Tool);
        if (gate is not null)
            Assert.Equal(gate, JsonSerializer.SerializeToElement(routed.Value.Input).GetProperty("gate").GetString());
    }
}
