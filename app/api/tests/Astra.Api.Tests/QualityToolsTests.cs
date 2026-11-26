using Astra.Api.Validation;
using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Q3: SpotBugs/FindSecBugs, PMD and Checkstyle findings from the maven sidecar join the QUALITY gate.</summary>
public class QualityToolsTests
{
    private static MavenClient.ToolFinding T(string tool, string rule, string category, string severity, string file = "src/main/java/com/x/Repo.java", int? line = 12) =>
        new(tool, rule, category, severity, file, line, "message");

    [Fact]
    public void Tool_findings_keep_the_package_layout_and_are_marked_as_tool_findings()
    {
        var f = QualityValidator.ToFindings([T("spotbugs", "SPOTBUGS-SQL_INJECTION_JDBC", "security", "critical")], "backend/");
        var one = Assert.Single(f);
        Assert.Equal("backend/src/main/java/com/x/Repo.java", one.File);
        Assert.Equal("tool", one.Source);
        Assert.Contains("FindSecBugs", one.Why);
        Assert.Contains("SQL_INJECTION_JDBC", one.Fix);
    }

    [Fact]
    public void A_critical_security_finding_from_a_tool_fails_the_gate_but_style_findings_do_not()
    {
        var critical = QualityValidator.ToFindings([T("spotbugs", "SPOTBUGS-SQL_INJECTION_JDBC", "security", "critical")], "");
        Assert.False(QualityRules.Summarise(critical).Passed);

        var style = QualityValidator.ToFindings([T("checkstyle", "CHECKSTYLE-AvoidStarImport", "standards", "minor"), T("pmd", "PMD-GodClass", "maintainability", "minor")], "");
        Assert.True(QualityRules.Summarise(style).Passed);
    }

    [Fact]
    public void A_finding_with_an_unknown_severity_is_ignored()
    {
        Assert.Empty(QualityValidator.ToFindings([T("pmd", "PMD-X", "standards", "weird")], ""));
    }
}
