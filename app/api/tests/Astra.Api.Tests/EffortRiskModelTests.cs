using Astra.Api.Assessment;
using Xunit;

namespace Astra.Api.Tests;

public class EffortRiskModelTests
{
    private static EffortRiskModel.Inputs Baseline(int routines = 776, string language = "cpp") => new(
        Routines: routines, Language: language, CyclicFraction: 0, HotspotShare: 0, SharedStorageFraction: 0,
        ComplexFraction: 0, FlaggedFraction: 0, ExternalCallees: 0, UiFlagged: false, DataFlagged: false);

    [Fact]
    public void Clean_estate_is_base_weeks_times_spread()
    {
        var r = EffortRiskModel.Compute(Baseline());
        var baseWeeks = 776 / 35.0;
        Assert.InRange(r.Effort.PersonWeeks.Low, baseWeeks * 0.8 - 0.01, baseWeeks * 0.8 + 0.01);
        Assert.InRange(r.Effort.PersonWeeks.High, baseWeeks * 1.4 - 0.01, baseWeeks * 1.4 + 0.01);
        Assert.Equal("faithful-1to1", r.Recommendation.Mode);
        Assert.Equal("dotnet10", r.Recommendation.TargetStack);
        Assert.Equal(1, r.Risk.Score);
    }

    [Fact]
    public void Multipliers_are_bounded_even_for_pathological_inputs()
    {
        // A globals-heavy C++ estate once produced a "coupling ratio" of 1012
        // and an XL estimate in the thousands of person-weeks. Every factor
        // must clamp to [0, 1], so the worst case is a known ceiling.
        var worst = Baseline() with
        {
            CyclicFraction = 50, HotspotShare = 12, SharedStorageFraction = 1012, ComplexFraction = 7, FlaggedFraction = 3,
        };
        var r = EffortRiskModel.Compute(worst);
        var baseWeeks = 776 / 35.0;
        var ceiling = baseWeeks * 1.5 * 1.3 * 1.2 * 1.4 * 1.3 * 1.4;
        Assert.True(r.Effort.PersonWeeks.High <= ceiling + 0.01, $"high {r.Effort.PersonWeeks.High} exceeds ceiling {ceiling}");
        Assert.True(r.Effort.PersonWeeks.High < 200, "worst case must stay in a plausible range");
    }

    [Fact]
    public void Cycles_on_a_large_estate_recommend_strangler()
    {
        var r = EffortRiskModel.Compute(Baseline(routines: 1800, language: "cobol") with { CyclicFraction = 0.2 });
        Assert.Equal("strangler", r.Recommendation.Mode);
        Assert.Contains(r.Risk.Drivers, d => d.Contains("call cycles"));
        Assert.True(r.Risk.Score >= 2);
    }

    [Fact]
    public void Ui_flags_recommend_modernize()
    {
        var r = EffortRiskModel.Compute(Baseline(routines: 300, language: "delphi") with { UiFlagged = true, FlaggedFraction = 0.3 });
        Assert.Equal("modernize", r.Recommendation.Mode);
    }

    [Fact]
    public void Unknown_language_uses_the_default_throughput()
    {
        var r = EffortRiskModel.Compute(Baseline(routines: 400, language: "brainfuck"));
        Assert.Contains(r.Effort.Drivers, d => d.Contains("~40/person-week"));
        Assert.Null(r.Recommendation.TargetStack);
    }
}
