using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Replaces the legacy ISAM callouts INV_READ / INV_WRITE.
/// </summary>
public interface IRollRepository
{
    Task<Roll?> GetByIdAsync(string rollId, CancellationToken ct = default);
    Task SaveAsync(Roll roll, string operatorId, CancellationToken ct = default);
}
