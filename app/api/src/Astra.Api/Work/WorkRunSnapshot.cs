using Astra.Api.Persistence.Entities;

namespace Astra.Api.Work;

/// <summary>Item counts of one stage of a run.</summary>
public sealed record StageCounts(string Stage, int Total, int Pending, int Running, int Succeeded, int Failed, int Skipped, int Waiting, int Cancelled, int Cap)
{
    public int Open => Pending + Running + Waiting;
}

public sealed record WorkItemBrief(Guid Id, string Kind, Guid SubroutineId, string Name, string State, string? Error);

/// <summary>Serial-equivalent against parallel agent time, from the machine items' own
/// start and end times. Time spent waiting for a person is in neither figure.</summary>
public sealed record WorkMetrics(
    long ElapsedMs, long SerialMs, long BusyMs, double Speedup, int PeakConcurrency,
    int Items, int Calls, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>Everything the progress card, the Narrator and the REST routes say about a run,
/// read from <c>work_runs</c> / <c>work_items</c> so it is the same after a restart.</summary>
public sealed record WorkRunSnapshot(
    Guid RunId, string Kind, string Label, string State, Guid CorpusId, Guid? ConversationId,
    string? TargetStack, decimal? BudgetUsd, decimal CostUsd, int Calls, long InputTokens, long OutputTokens,
    int Routines, IReadOnlyList<StageCounts> Stages,
    IReadOnlyList<WorkItemBrief> Running, IReadOnlyList<WorkItemBrief> WaitingForSignoff, IReadOnlyList<WorkItemBrief> Failures,
    int WaitingForSignoffCount, int FailedCount, string Summary, WorkMetrics Metrics,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>Items an agent can still act on without a person.</summary>
    public int MachineOpen => Stages.Where(s => !WorkStages.IsHuman(s.Stage)).Sum(s => s.Running) + MachineReady;

    /// <summary>Pending machine items whose predecessors are done.</summary>
    public int MachineReady { get; init; }

    public bool IsTerminal => State is "COMPLETED" or "CANCELLED";
    /// <summary>Nothing for an agent to do until somebody signs.</summary>
    public bool IdleOnPeople => State == "RUNNING" && MachineOpen == 0 && WaitingForSignoffCount > 0;

    public int TotalItems => Stages.Sum(s => s.Total);
    public int ClosedItems => Stages.Sum(s => s.Total - s.Open);
}

/// <summary>Pure text and arithmetic over a run's rows.</summary>
public static class WorkRunText
{
    /// <summary>"34 of 80 extracted, 6 waiting for sign-off, 2 failed, cost $1.20".</summary>
    public static string Progress(IReadOnlyList<StageCounts> stages, int waitingForSignoff, int failed, decimal costUsd)
    {
        var parts = new List<string>();
        foreach (var s in stages.OrderBy(s => WorkStages.IndexOf(s.Stage)))
        {
            if (s.Total == 0 || WorkStages.IsHuman(s.Stage)) continue;
            parts.Add($"{s.Succeeded} of {s.Total} {WorkStages.Done(s.Stage)}");
        }
        var signed = stages.FirstOrDefault(s => WorkStages.IsHuman(s.Stage));
        if (signed is { Total: > 0, Succeeded: > 0 }) parts.Add($"{signed.Succeeded} of {signed.Total} signed");
        if (waitingForSignoff > 0) parts.Add($"{waitingForSignoff} waiting for sign-off");
        var skipped = stages.Sum(s => s.Skipped);
        if (failed > 0) parts.Add($"{failed} failed" + (skipped > 0 ? $" ({skipped} later steps skipped)" : ""));
        parts.Add($"cost ${costUsd:0.00}");
        return string.Join(", ", parts);
    }

    public static WorkMetrics Metrics(WorkRun run, IReadOnlyList<WorkItem> items, DateTimeOffset now)
    {
        var spans = items
            .Where(i => !WorkStages.IsHuman(i.Kind) && i.StartedAt is not null && i.CompletedAt is not null && i.CompletedAt >= i.StartedAt)
            .Select(i => (Start: i.StartedAt!.Value, End: i.CompletedAt!.Value))
            .OrderBy(s => s.Start)
            .ToList();

        long serial = spans.Sum(s => (long)(s.End - s.Start).TotalMilliseconds);

        // Union of the intervals: time during which at least one agent was working.
        long busy = 0;
        DateTimeOffset? open = null, close = null;
        foreach (var (start, end) in spans)
        {
            if (open is null) { open = start; close = end; continue; }
            if (start <= close) { if (end > close) close = end; continue; }
            busy += (long)(close!.Value - open.Value).TotalMilliseconds;
            open = start; close = end;
        }
        if (open is not null) busy += (long)(close!.Value - open.Value).TotalMilliseconds;

        var peak = 0;
        var live = 0;
        // Ends sort before starts at the same instant, so back-to-back items do not count as overlapping.
        foreach (var (_, delta) in spans.SelectMany(s => new[] { (At: s.Start, Delta: 1), (At: s.End, Delta: -1) })
                     .OrderBy(e => e.At).ThenBy(e => e.Delta))
        {
            live += delta;
            peak = Math.Max(peak, live);
        }

        var elapsed = (long)((run.CompletedAt ?? now) - run.CreatedAt).TotalMilliseconds;
        return new WorkMetrics(
            elapsed, serial, busy,
            busy > 0 ? Math.Round((double)serial / busy, 2) : 1,
            peak, spans.Count, run.Calls, run.InputTokens, run.OutputTokens, run.CostUsd);
    }

    public static string Duration(long ms) =>
        ms < 1000 ? $"{ms} ms"
        : ms < 90_000 ? $"{ms / 1000.0:0.#} s"
        : $"{ms / 60_000.0:0.#} min";
}
