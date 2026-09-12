using Astra.Api.Runs;

namespace Astra.Api.Docs;

/// <summary>
/// Thin string-log façade over <see cref="RunEventBus"/>, kept so the
/// docs/harmonisation orchestrators and the <c>/docs/runs/{id}/logs</c> SSE
/// route work unchanged. New code should publish structured events on the
/// bus directly; this adapter emits <c>log</c> events only.
/// </summary>
public sealed class DocRunLogger
{
    private readonly RunEventBus _bus;

    public DocRunLogger(RunEventBus bus)
    {
        _bus = bus;
    }

    public void Log(Guid runId, string message) => _bus.Log(runId, "run", "", message);

    public IAsyncEnumerable<string> SubscribeAsync(Guid runId, CancellationToken ct)
        => _bus.SubscribeMessagesAsync(runId, 0, ct);

    public void Complete(Guid runId) => _bus.Complete(runId);
}
