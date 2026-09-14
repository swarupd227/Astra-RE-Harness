using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Roll-stock persistence boundary. Maps the IO-1 / IO-2 claims from
/// the signed COBOL spec — VSAM READ + REWRITE on the ROLL-MASTER file.
/// Adapter implementation supplies the real VSAM access layer per
/// deployment.
/// </summary>
public interface IRollRepository
{
    Task<Roll?> ReadAsync(string rollId, CancellationToken ct = default);
    Task RewriteAsync(Roll roll, CancellationToken ct = default);
}

/// <summary>One row from ROLL-MASTER. Schema mirrors the COBOL record.</summary>
public sealed record Roll(string Id, decimal OnHandLf, int Status, string GradeCd, bool Locked);
