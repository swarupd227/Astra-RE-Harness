using System.Threading.Channels;

namespace Astra.Api.Docs;

/// <summary>
/// In-process log bus for active doc-generation runs.
/// One unbounded Channel per run.  Late subscribers drain any buffered
/// messages then block until more arrive or the writer is completed.
/// Completed channels are kept in the dictionary so a subscriber that
/// arrives after <see cref="Complete"/> still gets an immediate EOF.
/// </summary>
public sealed class DocRunLogger : IDisposable
{
    private readonly Dictionary<Guid, Channel<string>> _channels = new();
    private readonly object _lock = new();

    public void Log(Guid runId, string message)
        => GetOrCreate(runId).Writer.TryWrite(message);

    public IAsyncEnumerable<string> SubscribeAsync(Guid runId, CancellationToken ct)
        => GetOrCreate(runId).Reader.ReadAllAsync(ct);

    public void Complete(Guid runId)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(runId, out var ch))
                ch.Writer.TryComplete();
            // Intentionally keep channel in dictionary so late subscribers
            // get an immediate EOF rather than waiting forever on a fresh channel.
        }
    }

    private Channel<string> GetOrCreate(Guid runId)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(runId, out var ch))
                _channels[runId] = ch = Channel.CreateUnbounded<string>();
            return ch;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var ch in _channels.Values)
                ch.Writer.TryComplete();
        }
    }
}
