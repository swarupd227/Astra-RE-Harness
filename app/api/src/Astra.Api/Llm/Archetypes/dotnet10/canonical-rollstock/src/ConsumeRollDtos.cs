namespace Demo.RollStock;

/// <summary>
/// Result code for <see cref="ConsumeRollService.ConsumeAsync"/>.
/// Mirrors the legacy CONSUME_ROLL RESULT_CD contract.
/// </summary>
public enum ConsumeRollResult
{
    Ok = 0,
    NotFound = 1,
    Insufficient = 2,
    Locked = 3,
}

/// <summary>Roll-stock record. Maps the INVMASTR row read by INV_READ.</summary>
public sealed record Roll(
    string Id,
    decimal OnHandLf,
    int Status,
    string GradeCode,
    bool Locked);
