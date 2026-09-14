using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>Replaces the EMIT_EVENT callout (downstream event channel).</summary>
public interface IEventNotifier
{
    Task NotifyAsync(
        string eventCode,
        string rollId,
        string gradeCode,
        decimal newLf,
        CancellationToken ct = default);
}
