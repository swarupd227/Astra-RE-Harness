using System.Text;
using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

public class DocsBannedPhraseTests
{
    [Fact]
    public void Find_IsCaseInsensitive_AndCounts()
    {
        var hits = DocBannedPhrases.Find(
            "It is worth noting that X. Overall, we leverage Y and then Leverage Z.");

        Assert.Contains(hits, h => h.Phrase == "it is worth noting" && h.Count == 1);
        Assert.Contains(hits, h => h.Phrase == "overall," && h.Count == 1);
        Assert.Contains(hits, h => h.Phrase == "leverage" && h.Count == 2);
    }

    [Fact]
    public void Find_IgnoresCodeSpansAndFences()
    {
        var md = "Call `ROBUST` first.\n\n```fortran\n      CALL ROBUST(X)\n```\n";
        Assert.Empty(DocBannedPhrases.Find(md));

        Assert.Single(DocBannedPhrases.Find("A robust design."));
    }

    [Fact]
    public void Find_RespectsWordBoundaries()
    {
        Assert.Empty(DocBannedPhrases.Find("The robustness test delved into nothing."));
        Assert.Single(DocBannedPhrases.Find("We delve into it."));
    }

    [Fact]
    public void Find_HandlesCurlyApostrophes()
    {
        var hits = DocBannedPhrases.Find("It’s worth noting this.");
        Assert.Contains(hits, h => h.Phrase == "it's worth noting");
    }

    [Fact]
    public void RunChecks_FlagsEmptySection_BannedPhrase_AndLongTable()
    {
        var sb = new StringBuilder();
        sb.Append("# Title\n\n## Empty\n\n## Filled\n\nA robust paragraph.\n\n| a | b |\n|---|---|\n");
        for (var i = 0; i < 14; i++) sb.Append("| r").Append(i).Append(" | x |\n");

        var checks = DocCriticPass.RunChecks(sb.ToString(), null, Array.Empty<string>(), isCatalog: false, requireCitations: false);

        var empty = checks.Single(c => c.Name == "empty-sections");
        Assert.False(empty.Passed);
        Assert.Contains(empty.Problems, p => p.Contains("'Empty'"));

        var banned = checks.Single(c => c.Name == "banned-phrases");
        Assert.False(banned.Passed);
        Assert.Contains(banned.Problems, p => p.Contains("robust"));

        Assert.False(checks.Single(c => c.Name == "table-length").Passed);

        // Catalogs are exempt from the table limit.
        var catalog = DocCriticPass.RunChecks(sb.ToString(), null, Array.Empty<string>(), isCatalog: true, requireCitations: false);
        Assert.True(catalog.Single(c => c.Name == "table-length").Passed);
    }

    [Fact]
    public void RunChecks_ParentHeadingWithChildContent_IsNotEmpty()
    {
        var md = "# T\n\n## Subsystems\n\n### Solvers\n\nText about solvers.\n\n## Risks\n\n- one\n";
        var checks = DocCriticPass.RunChecks(md, null, Array.Empty<string>(), isCatalog: false, requireCitations: false);
        Assert.True(checks.Single(c => c.Name == "empty-sections").Passed);
    }

    [Fact]
    public void RunChecks_RoutineCoverage_UsesWholeIdentifiers()
    {
        var md = "# T\n\nThe file provides `DGEMM` and DGEMMX.\n";
        var checks = DocCriticPass.RunChecks(md, null, new[] { "DGEMM", "DTRSM" }, isCatalog: false, requireCitations: false);

        var coverage = checks.Single(c => c.Name == "routine-coverage");
        Assert.False(coverage.Passed);
        Assert.Single(coverage.Problems);
        Assert.Contains("DTRSM", coverage.Problems[0]);
    }

    [Fact]
    public void RunChecks_CleanDocument_PassesEverything()
    {
        var md = "# lmder.f — driver\n\nSolves the problem.\n\n## Routine map\n\n- `LMDER` — driver — [minpack/lmder.f:L1–L452]\n";
        var index = new DocCitations.CorpusIndex(new[] { new DocCitations.RoutineSpan("minpack/lmder.f", "LMDER", 1, 452) });
        var checks = DocCriticPass.RunChecks(md, index, new[] { "LMDER" }, isCatalog: false, requireCitations: true);
        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Name}: {string.Join("; ", c.Problems)}"));
    }
}
