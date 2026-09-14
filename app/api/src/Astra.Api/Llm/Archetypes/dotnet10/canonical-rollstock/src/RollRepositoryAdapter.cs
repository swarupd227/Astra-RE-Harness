using System;
using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Stub adapter — bind to the modern persistence layer (DB / event store).
/// </summary>
public sealed class RollRepositoryAdapter : IRollRepository
{
    public Task<Roll?> GetByIdAsync(string rollId, CancellationToken ct = default)
    {
        // TODO: replace with the modern data layer's read for INVMASTR.
        throw new NotImplementedException();
    }

    public Task SaveAsync(Roll roll, string operatorId, CancellationToken ct = default)
    {
        // TODO: replace with the modern data layer's write for INVMASTR.
        throw new NotImplementedException();
    }
}
