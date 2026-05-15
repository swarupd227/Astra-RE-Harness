using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Downstream-notification boundary. Maps the IO-2 claim from the
/// signed COBOL spec — emit INV-CHG when a successful CONSUME-ROLL
/// rewrites a ROLL-MASTER row.
/// </summary>
public interface IEventNotifier
{
    Task EmitInventoryChangedAsync(
        string rollId,
        string gradeCd,
        decimal newLf,
        CancellationToken ct = default);
}
