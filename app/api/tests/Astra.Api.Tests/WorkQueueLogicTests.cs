using Astra.Api.Auth;
using Astra.Api.Persistence.Entities;
using Astra.Api.Work;
using Xunit;

namespace Astra.Api.Tests;

public class WorkPlannerTests
{
    private static RoutineSnapshot R(string state, int wave = 1, string? name = null) =>
        new(Guid.NewGuid(), name ?? state, state, wave);

    private static readonly IReadOnlyList<string> All = WorkStages.Normalise(null);

    [Fact]
    public void AParsedRoutine_GetsTheWholeChain_EachStepDependingOnTheOneBefore()
    {
        var plan = WorkPlanner.Expand(new[] { R("PARSED", wave: 2) }, All);

        Assert.Equal(new[] { "extract", "signoff", "scaffold", "compile", "test-pack" }, plan.Items.Select(i => i.Kind));
        Assert.Empty(plan.Items[0].DependsOn);
        for (var i = 1; i < plan.Items.Count; i++)
            Assert.Equal(new[] { plan.Items[i - 1].Id }, plan.Items[i].DependsOn);
        Assert.Equal(20, plan.Items[0].Priority);
    }

    [Theory]
    [InlineData("DRAFT", "signoff")]
    [InlineData("IN_REVIEW", "signoff")]
    [InlineData("SIGNED", "scaffold")]
    [InlineData("SCAFFOLDED", "compile")]
    public void ARoutine_EntersTheChain_AtTheStageItsStateCallsFor(string state, string first)
    {
        var plan = WorkPlanner.Expand(new[] { R(state) }, All);
        Assert.Equal(first, plan.Items[0].Kind);
        Assert.Empty(plan.Items[0].DependsOn);
    }

    [Theory]
    [InlineData("EXTRACTING")]
    [InlineData("SCAFFOLDING")]
    [InlineData("COMMITTED")]
    public void BusyOrCommittedRoutines_AreSkipped_WithAReason(string state)
    {
        var plan = WorkPlanner.Expand(new[] { R(state) }, All);
        Assert.Empty(plan.Items);
        Assert.NotEmpty(Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void LowerWaves_ComeFirst()
    {
        var plan = WorkPlanner.Expand(new[] { R("PARSED", 3, "late"), R("PARSED", 1, "leaf") }, All);
        Assert.Equal("leaf", plan.Items.Where(i => i.Kind == "extract").OrderBy(i => i.Priority).First().SubjectName);
    }

    [Fact]
    public void TheSignOffWait_CannotBeLeftOut_BetweenDraftingAndGenerating()
    {
        var stages = WorkStages.Normalise(new[] { "extract", "scaffold" });
        Assert.Equal(new[] { "extract", "signoff", "scaffold" }, stages);
    }

    [Theory]
    [InlineData("PARSED")]
    [InlineData("DRAFT")]
    [InlineData("IN_REVIEW")]
    public void GenerateOnly_NeverQueuesCodeFor_AnUnsignedRoutine(string state)
    {
        var plan = WorkPlanner.Expand(new[] { R(state) }, new[] { "scaffold" });
        Assert.DoesNotContain(plan.Items, i => i.Kind == "scaffold");
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void GatesOnly_NeedGeneratedCode()
    {
        var plan = WorkPlanner.Expand(new[] { R("SIGNED"), R("SCAFFOLDED") }, new[] { "compile", "test-pack" });
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(1, plan.RoutineCount);
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void OnlyEngineers_QueueCode_AdminsMayQueueDrafts_NobodyElseAnything()
    {
        var drafts = new[] { "extract", "signoff" };
        Assert.True(WorkRunService.MayRun(Persona.Engineer, All));
        Assert.True(WorkRunService.MayRun(Persona.Admin, drafts));
        Assert.False(WorkRunService.MayRun(Persona.Admin, All));
        Assert.False(WorkRunService.MayRun(Persona.Sme, drafts));
        Assert.False(WorkRunService.MayRun(Persona.Observer, drafts));
        Assert.False(WorkRunService.MayControl(Persona.Sme));
    }
}

public class WorkRunSnapshotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorkRun Run(string state = "RUNNING") => new()
    {
        Id = Guid.NewGuid(), Kind = "wave", CorpusId = Guid.NewGuid(), Label = "Wave 1 · demo", State = state,
        CapsJson = """{"extract":4}""", CreatedAt = T0,
    };

    private static WorkItem Item(string kind, string state, Guid? subject = null, double? start = null, double? end = null, params Guid[] deps) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, State = state, SubjectId = subject ?? Guid.NewGuid(), SubjectName = kind,
        DependsOn = deps,
        StartedAt = start is { } s ? T0.AddSeconds(s) : null,
        CompletedAt = end is { } e ? T0.AddSeconds(e) : null,
    };

    [Fact]
    public void ASignOff_IsWaiting_OnlyOnceItsDraftExists()
    {
        var done = Item("extract", "SUCCEEDED");
        var running = Item("extract", "RUNNING");
        var items = new[]
        {
            done, running,
            Item("signoff", "WAITING", deps: done.Id),
            Item("signoff", "WAITING", deps: running.Id),
        };

        var s = WorkQueue.Snapshot(Run(), items, T0.AddMinutes(1));

        Assert.Equal(1, s.WaitingForSignoffCount);
        Assert.False(s.IdleOnPeople); // an agent is still working
        Assert.Equal(4, s.Stages.Single(c => c.Stage == "extract").Cap);
    }

    [Fact]
    public void WithOnlySignOffsLeft_TheRunIsIdleOnPeople()
    {
        var draft = Item("extract", "SUCCEEDED");
        var sign = Item("signoff", "WAITING", deps: draft.Id);
        var code = Item("scaffold", "PENDING", deps: sign.Id);

        var s = WorkQueue.Snapshot(Run(), new[] { draft, sign, code }, T0);

        Assert.True(s.IdleOnPeople);
        Assert.Equal(0, s.MachineOpen);
        Assert.Equal("1 of 1 extracted, 0 of 1 generated, 1 waiting for sign-off, cost $0.00", s.Summary);
    }

    [Fact]
    public void Metrics_CompareParallelBusyTime_WithTheSerialSum_AndIgnoreTheHumanWait()
    {
        var run = Run("COMPLETED");
        run.CompletedAt = T0.AddHours(2);
        var items = new[]
        {
            Item("extract", "SUCCEEDED", start: 0, end: 10),
            Item("extract", "SUCCEEDED", start: 0, end: 10),
            Item("extract", "SUCCEEDED", start: 5, end: 15),
            Item("signoff", "SUCCEEDED", start: 15, end: 3600),   // a person: not agent time
            Item("scaffold", "SUCCEEDED", start: 3600, end: 3605),
        };

        var m = WorkRunText.Metrics(run, items, T0);

        Assert.Equal(35_000, m.SerialMs);
        Assert.Equal(20_000, m.BusyMs);
        Assert.Equal(1.75, m.Speedup);
        Assert.Equal(3, m.PeakConcurrency);
        Assert.Equal(4, m.Items);
        Assert.Equal(7_200_000, m.ElapsedMs);
    }

    [Fact]
    public void BackToBackSteps_DoNotCountAsParallel()
    {
        var m = WorkRunText.Metrics(Run(), new[]
        {
            Item("compile", "SUCCEEDED", start: 0, end: 5),
            Item("compile", "SUCCEEDED", start: 5, end: 10),
        }, T0);

        Assert.Equal(1, m.PeakConcurrency);
        Assert.Equal(1, m.Speedup);
    }

    [Fact]
    public void Progress_NamesFailuresAndTheStepsSkippedBehindThem()
    {
        var items = new[] { Item("extract", "FAILED"), Item("signoff", "SKIPPED"), Item("scaffold", "SKIPPED") };
        var s = WorkQueue.Snapshot(Run(), items, T0);
        Assert.Contains("1 failed (2 later steps skipped)", s.Summary);
    }
}

public class WorkNarrationTests
{
    private static WorkRunSnapshot Snapshot(
        string state = "RUNNING", int extractDone = 0, int extractOpen = 0, int waiting = 0, int ready = 0, string summary = "progress")
    {
        var stages = new List<StageCounts>
        {
            new("extract", extractDone + extractOpen, extractOpen, 0, extractDone, 0, 0, 0, 0, 4),
        };
        var now = DateTimeOffset.UtcNow;
        return new WorkRunSnapshot(Guid.NewGuid(), "wave", "Wave 1", state, Guid.NewGuid(), Guid.NewGuid(), null, null, 0, 0, 0, 0,
            4, stages, new List<WorkItemBrief>(), new List<WorkItemBrief>(), new List<WorkItemBrief>(),
            waiting, 0, summary, new WorkMetrics(0, 0, 0, 1, 0, 0, 0, 0, 0, 0), now, null)
        { MachineReady = ready };
    }

    [Fact]
    public void Halfway_IsSaidOnce()
    {
        var n = new WorkNarration();
        Assert.Null(n.Next(Snapshot(extractDone: 1, extractOpen: 5, ready: 5)));
        Assert.Contains("halfway", n.Next(Snapshot(extractDone: 3, extractOpen: 3, ready: 3))!.Markdown);
        Assert.Null(n.Next(Snapshot(extractDone: 4, extractOpen: 2, ready: 2)));
    }

    [Fact]
    public void TheSignOffWait_IsAnnounced_WithTheReviewQueue_AndNotRepeatedAsItShrinks()
    {
        var n = new WorkNarration();
        var first = n.Next(Snapshot(extractDone: 6, waiting: 6))!;
        Assert.True(first.ShowReviewQueue);
        Assert.Contains("until an SME signs", first.Markdown);
        Assert.Contains(first.Chips, c => c.Intent == "Show the review queue");
        Assert.Null(n.Next(Snapshot(extractDone: 6, waiting: 6)));
        Assert.Null(n.Next(Snapshot(extractDone: 6, waiting: 5)));
    }

    [Fact]
    public void APause_IsReportedWithItsReason_AndOffersResumeAndCancel()
    {
        var n = new WorkNarration();
        var note = n.Next(Snapshot("PAUSED", extractOpen: 2, summary: "Paused: the cost ceiling of $1.00 was reached."))!;
        Assert.Contains("cost ceiling", note.Markdown);
        Assert.Equal(new[] { "Resume", "Cancel the run", "Run progress" }, note.Chips.Select(c => c.Label));
        Assert.Null(n.Next(Snapshot("PAUSED", extractOpen: 2)));
    }
}
