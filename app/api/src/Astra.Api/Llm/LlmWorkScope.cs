using System.Collections.Concurrent;
using Astra.Api.Persistence.Entities;

namespace Astra.Api.Llm;

/// <summary>Which limiter lane a model call belongs to. A person waiting on a
/// reply (the copilot, a single extract they clicked) is Interactive; queued
/// per-routine work from a wave is Batch and yields to it.</summary>
public enum LlmLane { Interactive, Batch }

/// <summary>Thrown by the limiter when a run has spent its cost ceiling, so
/// the next model call is refused instead of silently overspending.</summary>
public sealed class LlmBudgetExceededException : Exception
{
    public Guid? RunId { get; }
    public decimal CeilingUsd { get; }
    public decimal SpentUsd { get; }

    public LlmBudgetExceededException(Guid? runId, decimal ceilingUsd, decimal spentUsd)
        : base($"The run's cost ceiling of ${ceilingUsd:0.00} was reached (spent ${spentUsd:0.00}).")
    {
        RunId = runId;
        CeilingUsd = ceilingUsd;
        SpentUsd = spentUsd;
    }
}

/// <summary>Spend of one work run, shared by every item of the run that is
/// executing in this process. The ceiling is checked before each call.</summary>
public sealed class RunBudget
{
    private readonly object _gate = new();
    private decimal _spent;
    private int _calls;
    private long _in;
    private long _out;

    public Guid RunId { get; }
    public decimal? CeilingUsd { get; set; }

    public RunBudget(Guid runId, decimal? ceilingUsd, decimal alreadySpent = 0)
    {
        RunId = runId;
        CeilingUsd = ceilingUsd;
        _spent = alreadySpent;
    }

    public decimal SpentUsd { get { lock (_gate) return _spent; } }
    public bool Exhausted => CeilingUsd is { } c && SpentUsd >= c;

    public void Add(decimal usd, int input, int output)
    {
        lock (_gate) { _spent += usd; _calls++; _in += input; _out += output; }
    }
}

/// <summary>
/// Ambient description of the work a model call is made for: its lane, the
/// run budget it counts against, and per-item usage counters. Flows with the
/// async context, so no pipeline signature had to change — the limiter reads
/// it at acquire time and the DbContext reports every saved <see cref="LlmCall"/>
/// to it.
/// </summary>
public sealed class LlmWorkScope
{
    private static readonly AsyncLocal<LlmWorkScope?> CurrentScope = new();
    private static readonly ConcurrentDictionary<Guid, RunBudget> Budgets = new();

    public static LlmWorkScope? Current => CurrentScope.Value;

    public LlmLane Lane { get; }
    public RunBudget? Budget { get; }

    private int _calls;
    private long _in;
    private long _out;
    private long _costMicros;

    private LlmWorkScope(LlmLane lane, RunBudget? budget)
    {
        Lane = lane;
        Budget = budget;
    }

    public int Calls => Volatile.Read(ref _calls);
    public long InputTokens => Interlocked.Read(ref _in);
    public long OutputTokens => Interlocked.Read(ref _out);
    public decimal CostUsd => Interlocked.Read(ref _costMicros) / 1_000_000m;

    /// <summary>The budget object for a run, created on first use in this process.</summary>
    public static RunBudget BudgetFor(Guid runId, decimal? ceilingUsd, decimal alreadySpent = 0)
    {
        var b = Budgets.GetOrAdd(runId, id => new RunBudget(id, ceilingUsd, alreadySpent));
        b.CeilingUsd = ceilingUsd;
        return b;
    }

    public static void ForgetBudget(Guid runId) => Budgets.TryRemove(runId, out _);

    public static LlmWorkScope Begin(LlmLane lane, RunBudget? budget = null)
    {
        var scope = new LlmWorkScope(lane, budget);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Called for every <see cref="LlmCall"/> the DbContext saves.</summary>
    internal void Add(LlmCall call)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _in, call.InputTokens + call.CacheReadTokens + call.CacheCreationTokens);
        Interlocked.Add(ref _out, call.OutputTokens);
        Interlocked.Add(ref _costMicros, (long)(call.CostUsd * 1_000_000m));
        Budget?.Add(call.CostUsd, call.InputTokens, call.OutputTokens);
    }
}

/// <summary>Fan-out point for "a model call just finished": updates the
/// ambient scope and tells the limiters (token-per-minute window).</summary>
public static class LlmUsage
{
    public static event Action<LlmCall>? Recorded;

    public static void Publish(LlmCall call)
    {
        LlmWorkScope.Current?.Add(call);
        Recorded?.Invoke(call);
    }
}
