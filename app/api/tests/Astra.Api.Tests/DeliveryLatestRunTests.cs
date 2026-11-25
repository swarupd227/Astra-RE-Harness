using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The Delivery tracker called a requirement "failing" if its routine had EVER failed a gate, even after the fix went
/// green (found on a project whose latest compile and test pack both passed). Only the latest run of each gate counts.
/// </summary>
public class DeliveryLatestRunTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_gate_that_failed_and_was_rerun_green_is_green()
    {
        var latest = RequirementsDeliveryService.LatestStatusPerGate(new[]
        {
            ("COMPILE", "PASSED", T0),
            ("TEST_PACK", "FAILED", T0.AddMinutes(1)),
            ("COMPILE", "PASSED", T0.AddMinutes(5)),
            ("TEST_PACK", "PASSED", T0.AddMinutes(6)),
        });
        Assert.DoesNotContain("FAILED", latest);
        Assert.Equal(2, latest.Count);
    }

    [Fact]
    public void A_gate_whose_latest_run_failed_still_counts_as_failed_whatever_came_before()
    {
        var latest = RequirementsDeliveryService.LatestStatusPerGate(new[]
        {
            ("TEST_PACK", "PASSED", T0),
            ("TEST_PACK", "FAILED", T0.AddMinutes(3)),
        });
        Assert.Equal(new[] { "FAILED" }, latest);
    }

    [Fact]
    public void No_runs_means_no_statuses() =>
        Assert.Empty(RequirementsDeliveryService.LatestStatusPerGate(Array.Empty<(string, string, DateTimeOffset)>()));
}
