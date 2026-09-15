using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Astra.Api.Runs;

/// <summary>
/// In-process, multi-subscriber event bus for agent runs.
///
/// Per run: a bounded ring buffer (so a late subscriber — or a browser
/// reconnecting with <c>Last-Event-ID</c> — can replay what it missed) plus
/// N bounded subscriber channels that each receive every event (the old
/// single-consumer channel silently split messages between two tabs).
/// Runs are evicted 30 minutes after completion or after 6 hours idle,
/// with an LRU cap, so the process no longer leaks one channel per run
/// forever.
/// </summary>
public sealed class RunEventBus : IDisposable
{
    private const int RingCapacity = 2_000;
    private const int SubscriberCapacity = 1_000;
    private const int MaxRuns = 200;
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleRetention = TimeSpan.FromHours(6);

    private readonly Dictionary<Guid, RunStream> _runs = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _evictor;

    public RunEventBus()
    {
        _evictor = Task.Run(EvictLoopAsync);
    }

    // ── Publishing ───────────────────────────────────────────────────────

    public RunEvent Publish(Guid runId, string agent, string stage, string type, object? data = null, string? message = null)
    {
        RunStream stream;
        lock (_lock)
        {
            if (!_runs.TryGetValue(runId, out stream!))
            {
                EvictIfOverCapacityLocked();
                stream = new RunStream();
                _runs[runId] = stream;
            }
        }

        RunEvent evt;
        List<Channel<RunEvent>> subscribers;
        lock (stream.Lock)
        {
            evt = new RunEvent(runId, agent, stage, type, DateTimeOffset.UtcNow, ++stream.LastSeq, data, message);
            stream.Ring.Enqueue(evt);
            while (stream.Ring.Count > RingCapacity) stream.Ring.Dequeue();
            stream.LastActivity = evt.Ts;
            subscribers = stream.Subscribers.ToList();
        }
        foreach (var ch in subscribers) ch.Writer.TryWrite(evt);
        return evt;
    }

    public void Log(Guid runId, string agent, string stage, string message) =>
        Publish(runId, agent, stage, "log", null, message);

    public void Progress(Guid runId, string agent, string stage, int done, int total, int failed = 0, int propagated = 0, double? etaSeconds = null) =>
        Publish(runId, agent, stage, "progress",
            new { done, total, failed, propagated, etaSeconds },
            $"{done}/{total}" + (failed > 0 ? $" ({failed} failed)" : ""));

    public void State(Guid runId, string agent, string state, string? summary = null) =>
        Publish(runId, agent, "", "state", new { state, summary }, summary);

    /// <summary>Terminal marker: completes every subscriber; the ring stays
    /// readable for late joiners until eviction.</summary>
    public void Complete(Guid runId)
    {
        RunStream? stream;
        lock (_lock) _runs.TryGetValue(runId, out stream);
        if (stream is null)
        {
            // Nothing was ever published — still record completion so a
            // subscriber arriving later gets EOF instead of hanging.
            Publish(runId, "run", "", "done");
            lock (_lock) _runs.TryGetValue(runId, out stream);
            if (stream is null) return;
        }
        List<Channel<RunEvent>> subscribers;
        lock (stream.Lock)
        {
            if (!stream.Completed)
            {
                stream.Completed = true;
                stream.CompletedAt = DateTimeOffset.UtcNow;
                var last = stream.Ring.LastOrDefault();
                if (last is null || last.Type != "done")
                {
                    var evt = new RunEvent(runId, "run", "", "done", DateTimeOffset.UtcNow, ++stream.LastSeq, null, null);
                    stream.Ring.Enqueue(evt);
                    foreach (var ch in stream.Subscribers) ch.Writer.TryWrite(evt);
                }
            }
            subscribers = stream.Subscribers.ToList();
        }
        foreach (var ch in subscribers) ch.Writer.TryComplete();
    }

    public bool IsCompleted(Guid runId)
    {
        lock (_lock)
        {
            return _runs.TryGetValue(runId, out var s) && s.Completed;
        }
    }

    // ── Subscribing ──────────────────────────────────────────────────────

    /// <summary>
    /// Replay every buffered event with <c>Seq &gt; afterSeq</c>, then stream
    /// live events until the run completes or the caller cancels. Safe for
    /// any number of concurrent subscribers.
    /// </summary>
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        Guid runId, long afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        // Subscribing before the first publish is allowed (a card can attach
        // in the same tick the run starts), so an unknown run gets a stream —
        // but only a short grace to say something. A run the bus never saw
        // (evicted after completion, or from before a restart) stays silent
        // and the subscription ends; before this it stayed open forever, every
        // finished run card in a thread did exactly that, and a few dozen of
        // them used up the browser's connections to the API so the composer's
        // own request never got through (seen in the golden demo).
        RunStream stream;
        var fresh = false;
        lock (_lock)
        {
            if (!_runs.TryGetValue(runId, out stream!))
            {
                EvictIfOverCapacityLocked();
                stream = new RunStream();
                _runs[runId] = stream;
                fresh = true;
            }
        }

        var channel = Channel.CreateBounded<RunEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        lock (stream.Lock)
        {
            foreach (var evt in stream.Ring)
            {
                if (evt.Seq > afterSeq) channel.Writer.TryWrite(evt);
            }
            if (stream.Completed) channel.Writer.TryComplete();
            else stream.Subscribers.Add(channel);
        }

        try
        {
            if (fresh)
            {
                bool spoke;
                using (var grace = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    grace.CancelAfter(UnknownRunGrace);
                    try { spoke = await channel.Reader.WaitToReadAsync(grace.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { spoke = false; }
                }
                if (!spoke) yield break;
            }
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            lock (stream.Lock) stream.Subscribers.Remove(channel);
        }
    }

    /// <summary>How long a subscription to a run the bus has never seen waits
    /// for its first event before ending.</summary>
    public static readonly TimeSpan UnknownRunGrace = TimeSpan.FromSeconds(5);

    /// <summary>Message-only view for legacy string-log consumers.</summary>
    public async IAsyncEnumerable<string> SubscribeMessagesAsync(
        Guid runId, long afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in SubscribeAsync(runId, afterSeq, ct))
        {
            if (evt.Type == "log" && evt.Message is not null) yield return evt.Message;
        }
    }

    // ── Eviction ─────────────────────────────────────────────────────────

    private async Task EvictLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var now = DateTimeOffset.UtcNow;
                lock (_lock)
                {
                    foreach (var (id, s) in _runs.ToList())
                    {
                        bool evict;
                        lock (s.Lock)
                        {
                            evict = (s.Completed && s.CompletedAt is { } c && now - c > CompletedRetention)
                                    || (!s.Completed && now - s.LastActivity > IdleRetention);
                        }
                        if (evict) RemoveLocked(id);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void EvictIfOverCapacityLocked()
    {
        if (_runs.Count < MaxRuns) return;
        var oldest = _runs
            .OrderBy(kv => kv.Value.LastActivity)
            .Take(_runs.Count - MaxRuns + 1)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in oldest) RemoveLocked(id);
    }

    private void RemoveLocked(Guid id)
    {
        if (!_runs.Remove(id, out var s)) return;
        lock (s.Lock)
        {
            foreach (var ch in s.Subscribers) ch.Writer.TryComplete();
            s.Subscribers.Clear();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            foreach (var id in _runs.Keys.ToList()) RemoveLocked(id);
        }
        _cts.Dispose();
    }

    private sealed class RunStream
    {
        public readonly object Lock = new();
        public readonly Queue<RunEvent> Ring = new();
        public readonly List<Channel<RunEvent>> Subscribers = new();
        public long LastSeq;
        public bool Completed;
        public DateTimeOffset? CompletedAt;
        public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;
    }
}
