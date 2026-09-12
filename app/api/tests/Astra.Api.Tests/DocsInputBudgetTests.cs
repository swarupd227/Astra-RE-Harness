using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

public class DocsInputBudgetTests
{
    // A: headline, big. B: standard, medium. C: standard, small.
    // Each has 100 chars of summary and a 400-char source slice.
    private static List<DocInputBudget.Item<string>> Items() => new()
    {
        new("A", "headline", 100, CoreChars: 100, ExtraChars: 400),
        new("B", "standard", 50, CoreChars: 100, ExtraChars: 400),
        new("C", "standard", 10, CoreChars: 100, ExtraChars: 400),
    };

    [Fact]
    public void UnderBudget_KeepsEverything_WithSource()
    {
        var fit = DocInputBudget.Fit(Items(), budgetTokens: 1_000);

        Assert.False(fit.Trimmed);
        Assert.Equal(3, fit.Kept.Count());
        Assert.All(fit.Placements, p => Assert.True(p.IncludeExtra));
        Assert.Equal(1_500, fit.TotalChars);
    }

    [Fact]
    public void OverBudget_RemovesSourceFromLowestTierSmallestFirst()
    {
        // 1100 chars: one 400-char slice must go — C is standard tier and smallest.
        var fit = DocInputBudget.Fit(Items(), budgetTokens: 275);

        Assert.Equal(1, fit.ExtrasDropped);
        Assert.Equal(0, fit.Dropped);
        var byName = fit.Placements.ToDictionary(p => p.Value);
        Assert.True(byName["A"].IncludeExtra);
        Assert.True(byName["B"].IncludeExtra);
        Assert.False(byName["C"].IncludeExtra);
        Assert.True(byName["C"].Included);
    }

    [Fact]
    public void StillOverBudget_DropsWholeRoutines_NeverChopsText()
    {
        // 240 chars: all slices go (300 left), then C is dropped whole (200).
        var fit = DocInputBudget.Fit(Items(), budgetTokens: 60);

        Assert.Equal(3, fit.ExtrasDropped);
        Assert.Equal(1, fit.Dropped);
        Assert.Equal(new[] { "A", "B" }, fit.Kept.Select(p => p.Value).ToArray());
        Assert.Equal(new[] { "C" }, fit.DroppedValues.ToArray());
        Assert.Equal(200, fit.TotalChars);
    }

    [Fact]
    public void FixedChars_CountAgainstTheBudget()
    {
        var fitWithout = DocInputBudget.Fit(Items(), budgetTokens: 375);              // 1500 chars: fits exactly
        var fitWith = DocInputBudget.Fit(Items(), budgetTokens: 375, fixedChars: 1);  // one over: trims

        Assert.False(fitWithout.Trimmed);
        Assert.True(fitWith.Trimmed);
    }

    [Fact]
    public void HeadlineOutranksStandard_RegardlessOfSize()
    {
        var items = new List<DocInputBudget.Item<string>>
        {
            new("small-headline", "headline", 5, 100),
            new("big-standard", "standard", 500, 100),
        };
        var fit = DocInputBudget.Fit(items, budgetTokens: 25); // 100 chars: one survives

        Assert.Equal(new[] { "small-headline" }, fit.Kept.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void TierRank_OrdersHeadlineAboveStandardAboveUnknown()
    {
        Assert.True(DocInputBudget.TierRank("headline") > DocInputBudget.TierRank("standard"));
        Assert.True(DocInputBudget.TierRank("standard") > DocInputBudget.TierRank(null));
        Assert.Equal(DocInputBudget.TierRank("HEADLINE"), DocInputBudget.TierRank("headline"));
    }

    [Fact]
    public void EstimateTokens_UsesFourCharsPerToken_RoundedUp()
    {
        Assert.Equal(0, DocInputBudget.EstimateTokens(0));
        Assert.Equal(1, DocInputBudget.EstimateTokens(1));
        Assert.Equal(1, DocInputBudget.EstimateTokens(4));
        Assert.Equal(2, DocInputBudget.EstimateTokens(5));
        Assert.Equal(600_000, DocInputBudget.TokensToChars(150_000));
    }

    [Fact]
    public void SourceSlice_CapsWithAnExplicitNote()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"line {i}").ToArray();
        var slice = DocSourceSlices.Slice(lines, lineStart: 11, lineEnd: 40, cap: 10);

        Assert.StartsWith("11: line 11\n", slice);
        Assert.Contains("20: line 20\n", slice);
        Assert.DoesNotContain("21: line 21", slice);
        Assert.Contains("source truncated: showing lines 11–20 of 11–40 (20 lines omitted)", slice);

        var whole = DocSourceSlices.Slice(lines, 11, 40, cap: 0);
        Assert.Contains("40: line 40\n", whole);
        Assert.DoesNotContain("truncated", whole);
    }
}
