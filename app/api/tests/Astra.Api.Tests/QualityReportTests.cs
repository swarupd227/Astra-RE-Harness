using Astra.Api.Copilot;
using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The quality report proves what was checked, from the stored gate runs only, for this version of the code.</summary>
public class QualityReportTests
{
    private static readonly DateTimeOffset Generated = new(2026, 11, 27, 2, 10, 0, TimeSpan.Zero);

    private const string QualityMetrics = """
        {"total":3,"blocking":0,"blockOn":["blocker","critical"],"truncated":false,
         "bySeverity":{"blocker":0,"critical":0,"major":2,"minor":1,"info":0},
         "findings":[
           {"rule":"DEP-GHSA-x","category":"security","severity":"major","file":"backend/requirements.txt","line":null,"message":"pytest 8.3.5 has a known vulnerability","why":"w","fix":"Upgrade pytest","source":"tool"},
           {"rule":"STD-PRINT","category":"standards","severity":"minor","file":"backend/app/api.py","line":12,"message":"print() <in> code","why":"w","fix":"Use logging","source":"rule"},
           {"rule":"AI-EDGE","category":"maintainability","severity":"major","file":"backend/app/calc.py","line":40,"message":"Zero days not handled","why":"w","fix":"Guard it","source":"ai"}],
         "sbom":{"components":17,"ecosystems":["PyPI","npm"]},"sbomUri":"minio://scaffolds/validation/x/sbom.cdx.json",
         "tools":{"ruff":{"ok":true,"count":5,"durationMs":12,"error":null},"bandit":{"ok":true,"count":0,"durationMs":181,"error":null},
                  "dependencies":{"ok":true,"count":1,"durationMs":0,"error":null}},
         "aiReview":{"status":"reviewed","kept":1,"proposed":2,"dropped":1,"model":"claude-sonnet","costUsd":0.1453}}
        """;

    private static QualityReportInput.Run Run(string stage, string status, int minutesAfter, string? metrics = null, string summary = "") =>
        new(Guid.NewGuid(), stage, status, summary == "" ? $"{stage} {status}" : summary, metrics, Generated.AddMinutes(minutesAfter), Generated.AddMinutes(minutesAfter + 1));

    private static QualityReportInput Input(string stack, params QualityReportInput.Run[] runs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "CalculateApiPremium", "vba", "Book.xlsm/Module1.bas", "Travel", stack, Generated,
            26, 1400, 0, null, "SIGNED", 41, "sme@example", Generated.AddDays(-1), "abc123", runs, "0ed3167", Generated.AddHours(1));

    [Fact]
    public void All_three_required_gates_passing_on_this_version_is_a_pass()
    {
        var r = QualityReportBuilder.Build(Input("react-python",
            Run("COMPILE", "PASSED", 1), Run("TEST_PACK", "PASSED", 2, """{"total":41,"passed":41,"failed":0,"skipped":0,"runner":"pytest+jest"}"""),
            Run("QUALITY", "PASSED", 3, QualityMetrics)));

        Assert.Equal("PASSED", r.Verdict.Status);
        Assert.Contains("signed spec", r.Verdict.Reason);
        Assert.Equal(41, r.Tests.Passed);
        Assert.Equal(17, r.Dependencies.Components);
        Assert.Equal(1, r.Dependencies.Vulnerable);
        Assert.True(r.Dependencies.SbomAvailable);
        Assert.Equal(1, r.Findings.BySource["ai"]);
        Assert.Empty(r.History);
    }

    [Fact]
    public void A_run_older_than_the_package_is_history_not_proof()
    {
        var r = QualityReportBuilder.Build(Input("react-python",
            Run("COMPILE", "PASSED", -30), Run("TEST_PACK", "PASSED", -29), Run("QUALITY", "FAILED", -28),
            Run("COMPILE", "PASSED", 1)));

        Assert.Equal("INCOMPLETE", r.Verdict.Status);
        Assert.Equal("Claim test pack and quality have not run on this version of the code.", r.Verdict.Reason);
        Assert.Equal("NOT_RUN", r.Gates.Single(g => g.Stage == "QUALITY").Status);
        Assert.Contains("earlier runs", r.Gates.Single(g => g.Stage == "QUALITY").Summary);
        Assert.Equal(3, r.History.Count);
        Assert.All(r.Checks.Where(c => c.Status is not ("not-applicable" or "not-available")), c => Assert.Equal("not-run", c.Status));
    }

    [Fact]
    public void Any_failed_gate_fails_the_report_and_is_named()
    {
        var r = QualityReportBuilder.Build(Input("react-python",
            Run("COMPILE", "FAILED", 1), Run("TEST_PACK", "PASSED", 2), Run("QUALITY", "PASSED", 3, QualityMetrics)));
        Assert.Equal("FAILED", r.Verdict.Status);
        Assert.StartsWith("Build", r.Verdict.Reason);
    }

    [Fact]
    public void Every_check_is_listed_with_its_version_even_when_it_does_not_apply()
    {
        var r = QualityReportBuilder.Build(Input("react-python", Run("QUALITY", "PASSED", 3, QualityMetrics)));
        string Status(string id) => r.Checks.Single(c => c.Id == id).Status;

        Assert.Equal("ran", Status("rules"));
        Assert.Equal("ran", Status("ruff"));
        Assert.Equal("0.8.4", r.Checks.Single(c => c.Id == "ruff").Version);
        Assert.Equal("ran", Status("bandit"));
        Assert.Equal("ran", Status("dependencies"));
        Assert.Equal("ran", Status("sbom"));
        Assert.Equal("ran", Status("ai-review"));
        Assert.Equal("claude-sonnet", r.Checks.Single(c => c.Id == "ai-review").Version);
        Assert.Equal("not-applicable", Status("spotbugs"));
        Assert.Equal("not-applicable", Status("dotnet-analyzers"));
        Assert.Equal("not-available", Status("eslint"));
        Assert.Contains(QualityRules.RuleSetFingerprint, r.Checks.Single(c => c.Id == "rules").Version);
    }

    [Fact]
    public void A_java_package_expects_the_java_tools_and_says_when_they_were_not_reported()
    {
        var r = QualityReportBuilder.Build(Input("java-spring", Run("QUALITY", "PASSED", 3,
            """{"total":0,"blocking":0,"findings":[],"tools":{"java-tools":{"ok":false,"count":0,"durationMs":0,"error":"the Java build service is down"}}}""")));
        var spot = r.Checks.Single(c => c.Id == "spotbugs");
        Assert.Equal("failed-to-run", spot.Status);
        Assert.Equal("the Java build service is down", spot.Note);
        Assert.Equal("not-run", r.Checks.Single(c => c.Id == "ai-review").Status);
        Assert.Equal("not-applicable", r.Checks.Single(c => c.Id == "ruff").Status);
    }

    [Fact]
    public void Blocking_findings_are_listed_first_and_ai_findings_never_block()
    {
        var r = QualityReportBuilder.Build(Input("react-python", Run("QUALITY", "FAILED", 3, """
            {"total":2,"blocking":1,"blockOn":["blocker","critical"],"findings":[
              {"rule":"AI-X","category":"security","severity":"critical","file":"a.py","line":1,"message":"m","source":"ai"},
              {"rule":"SEC-SECRET-KEY","category":"security","severity":"blocker","file":"b.py","line":2,"message":"m","source":"rule"}]}
            """)));
        Assert.Equal("SEC-SECRET-KEY", r.Findings.Listed[0].Rule);
        Assert.True(r.Findings.Listed[0].Blocking);
        Assert.False(r.Findings.Listed.Single(f => f.Source == "ai").Blocking);
        Assert.Equal("FAILED", r.Verdict.Status);
    }

    [Fact]
    public void The_html_escapes_what_it_shows_and_names_every_section()
    {
        var r = QualityReportBuilder.Build(Input("react-python",
            Run("COMPILE", "PASSED", 1), Run("TEST_PACK", "PASSED", 2), Run("QUALITY", "PASSED", 3, QualityMetrics)));
        var html = QualityReportHtml.Render(r);
        Assert.Contains("print() &lt;in&gt; code", html);
        Assert.DoesNotContain("<in>", html);
        foreach (var heading in new[] { "1. What was checked", "2. Gates", "3. Checks inside the quality gate", "4. Findings", "5. Libraries", "6. Tests" })
            Assert.Contains(heading, html);
        Assert.Contains("0ed3167", html);
        // For a look at the printed page: set QUALITY_REPORT_SAMPLE_DIR and render the file with weasyprint.
        if (Environment.GetEnvironmentVariable("QUALITY_REPORT_SAMPLE_DIR") is { Length: > 0 } dir)
            File.WriteAllText(Path.Combine(dir, "quality-report-sample.html"), html);
    }

    [Fact]
    public void The_rollup_puts_failures_first_and_counts_verdicts()
    {
        var pass = QualityReportBuilder.Build(Input("react-python",
            Run("COMPILE", "PASSED", 1), Run("TEST_PACK", "PASSED", 2), Run("QUALITY", "PASSED", 3, QualityMetrics)));
        var fail = QualityReportBuilder.Build(Input("react-python", Run("COMPILE", "FAILED", 1)));
        var roll = QualityRollup.From(Guid.NewGuid(), "Travel", [pass, fail], "dev", Generated);
        Assert.Equal(2, roll.Routines);
        Assert.Equal(1, roll.Passed);
        Assert.Equal(1, roll.Failed);
        Assert.Equal("FAILED", roll.Rows[0].Verdict);
        Assert.Contains("Travel", QualityReportHtml.Render(roll));
    }

    [Fact]
    public void A_provider_error_is_reported_by_its_status_and_message_not_its_json()
    {
        var r = QualityReportBuilder.Build(Input("react-python", Run("QUALITY", "PASSED", 3, """
            {"total":0,"blocking":0,"findings":[],"aiReview":{"status":"failed","kept":0,"proposed":0,"model":"claude-sonnet",
             "note":"The AI review could not run: Anthropic returned 401: {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"},\"request_id\":\"req_1\"}"}}
            """)));
        var ai = r.Checks.Single(c => c.Id == "ai-review");
        Assert.Equal("failed-to-run", ai.Status);
        Assert.Equal("The AI review could not run: Anthropic returned 401: invalid x-api-key", ai.Note);
    }

    [Fact]
    public void The_rule_set_fingerprint_is_stable() =>
        Assert.Matches("^[0-9a-f]{12}$", QualityRules.RuleSetFingerprint);

    [Theory]
    [InlineData("Give me the quality report for `CalculateApiPremium`", "routine")]
    [InlineData("What checks have been done? quality report please", "routine")]
    [InlineData("quality report for the whole programme", "programme")]
    public void The_mock_brain_reads_the_report_rather_than_running_the_gate(string text, string scope)
    {
        var routed = MockCopilotBrain.RouteOrNull(text);
        Assert.NotNull(routed);
        Assert.Equal("get_quality_report", routed!.Value.Tool);
        Assert.Contains($"scope = {scope}", routed.Value.Input.ToString());
    }
}
