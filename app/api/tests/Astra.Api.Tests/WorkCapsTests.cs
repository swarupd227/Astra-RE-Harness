using Astra.Api.Work;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Per-stage concurrency: set when a run starts and changeable when it is resumed, by one rule.</summary>
public class WorkCapsTests
{
    [Fact]
    public void A_new_run_gets_the_defaults_with_its_requested_caps()
    {
        var caps = WorkStages.MergeCaps(WorkStages.DefaultCaps, new Dictionary<string, int> { [WorkStages.Compile] = 4 });
        Assert.Equal(4, caps[WorkStages.Compile]);
        Assert.Equal(WorkStages.DefaultCaps[WorkStages.TestPack], caps[WorkStages.TestPack]);
        Assert.Equal(WorkStages.DefaultCaps[WorkStages.Extract], caps[WorkStages.Extract]);
    }

    [Fact]
    public void Resuming_with_caps_changes_only_what_is_asked_and_keeps_the_runs_own()
    {
        var current = new Dictionary<string, int> { [WorkStages.Extract] = 3, [WorkStages.Compile] = 2, [WorkStages.TestPack] = 2 };
        var caps = WorkStages.MergeCaps(current, new Dictionary<string, int> { [WorkStages.Compile] = 4, [WorkStages.TestPack] = 4 });
        Assert.Equal(4, caps[WorkStages.Compile]);
        Assert.Equal(4, caps[WorkStages.TestPack]);
        Assert.Equal(3, caps[WorkStages.Extract]);                                   // the run's own, not the default
        Assert.Equal(WorkStages.DefaultCaps[WorkStages.Scaffold], caps[WorkStages.Scaffold]);
    }

    [Fact]
    public void Unknown_stages_zero_and_huge_values_are_not_taken()
    {
        var caps = WorkStages.MergeCaps(WorkStages.DefaultCaps, new Dictionary<string, int>
        {
            ["signoff"] = 9, ["nope"] = 9, [WorkStages.Compile] = 0, [WorkStages.TestPack] = 500,
        });
        Assert.False(caps.ContainsKey("signoff"));
        Assert.False(caps.ContainsKey("nope"));
        Assert.Equal(WorkStages.DefaultCaps[WorkStages.Compile], caps[WorkStages.Compile]);
        Assert.Equal(WorkStages.MaxCap, caps[WorkStages.TestPack]);
    }
}
