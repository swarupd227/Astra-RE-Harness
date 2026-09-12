using Astra.Api.Runs;
using Xunit;

namespace Astra.Api.Tests;

public class RunEventBusTests
{
    [Fact]
    public async Task TwoSubscribers_BothReceiveEveryEvent()
    {
        using var bus = new RunEventBus();
        var runId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var a = CollectAsync(bus, runId, 0, cts.Token);
        var b = CollectAsync(bus, runId, 0, cts.Token);
        await Task.Delay(50); // let both subscribe

        bus.Log(runId, "survey", "survey", "one");
        bus.Progress(runId, "survey", "survey", 1, 3);
        bus.Log(runId, "survey", "survey", "two");
        bus.Complete(runId);

        var ra = await a;
        var rb = await b;
        Assert.Equal(new[] { "log", "progress", "log", "done" }, ra.Select(e => e.Type));
        Assert.Equal(new[] { "log", "progress", "log", "done" }, rb.Select(e => e.Type));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, ra.Select(e => e.Seq));
    }

    [Fact]
    public async Task LateJoiner_ReplaysAfterSeq()
    {
        using var bus = new RunEventBus();
        var runId = Guid.NewGuid();
        bus.Log(runId, "run", "", "one");
        bus.Log(runId, "run", "", "two");
        bus.Log(runId, "run", "", "three");
        bus.Complete(runId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var late = await CollectAsync(bus, runId, afterSeq: 1, cts.Token);
        Assert.Equal(new[] { "two", "three", null }, late.Select(e => e.Message));
    }

    [Fact]
    public async Task SubscribingToUnknownRun_EndsOnComplete()
    {
        using var bus = new RunEventBus();
        var runId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = CollectAsync(bus, runId, 0, cts.Token);
        await Task.Delay(50);
        bus.Complete(runId);
        var events = await task;
        Assert.Single(events);
        Assert.Equal("done", events[0].Type);
    }

    private static async Task<List<RunEvent>> CollectAsync(RunEventBus bus, Guid runId, long afterSeq, CancellationToken ct)
    {
        var list = new List<RunEvent>();
        await foreach (var e in bus.SubscribeAsync(runId, afterSeq, ct)) list.Add(e);
        return list;
    }
}
