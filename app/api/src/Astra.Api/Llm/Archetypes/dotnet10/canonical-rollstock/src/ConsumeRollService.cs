using System;
using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Roll-stock consumption service. Derived 1:1 from the signed
/// CONSUME_ROLL.FOR spec — every behavioural guard cites the
/// originating invariant (INV-*) or edge case (EC-*) so reviewers
/// can verify the translation without re-reading the Fortran.
/// </summary>
public sealed class ConsumeRollService : IConsumeRollService
{
    // Spec INV-5 magic constant — surfaced as a named field so the
    // implementation can swap to a per-grade lookup if Q-2 resolves.
    public const decimal MinRemainLf = 12.0m;

    // ROLL_STATUS = 9 (DEPLETED) sentinel from the source — Q-3
    // flagged the magic number; we keep the value but name it.
    private const int DepletedStatus = 9;

    private readonly IRollRepository _rolls;
    private readonly IEventNotifier _events;

    public ConsumeRollService(IRollRepository rolls, IEventNotifier events)
    {
        _rolls = rolls;
        _events = events;
    }

    /// <summary>
    /// Post a stock-consumption event for one roll. Replaces the
    /// Fortran CONSUME_ROLL subroutine; identical behaviour per
    /// the signed spec invariants and edge cases.
    /// </summary>
    public async Task<ConsumeRollResult> ConsumeAsync(
        string rollId,
        decimal usedLf,
        string operatorId,
        CancellationToken ct = default)
    {
        // INV-1 / EC-1: ISAM READ on INVMASTR keyed on ROLL_ID.
        // Source returns RESULT_CD = 1 (not_found) on IO_STAT ≠ 0.
        var roll = await _rolls.GetByIdAsync(rollId, ct);
        if (roll is null)
            return ConsumeRollResult.NotFound;

        // INV-2 / EC-2: locked rolls return Locked, no WRITE occurs.
        if (roll.Locked)
            return ConsumeRollResult.Locked;

        // INV-3 / EC-3: USED_LF strictly greater than ON_HAND_LF
        // returns Insufficient — no clamping, no persistence.
        if (usedLf > roll.OnHandLf)
            return ConsumeRollResult.Insufficient;

        // INV-4: NEW_LF = ON_HAND_LF − USED_LF.
        // Single REAL subtraction; Q-1 noted a negative USED_LF would
        // INCREASE stock — preserved as-is pending SME confirmation.
        var newLf = roll.OnHandLf - usedLf;

        // INV-5: NEW_LF below threshold marks the roll DEPLETED
        // (status 9) before the rewrite is persisted.
        var newStatus = newLf < MinRemainLf ? DepletedStatus : roll.Status;

        // INV-6: success path persists via ISAM REWRITE then emits
        // the INV_CHG event to downstream consumers.
        var updated = roll with { OnHandLf = newLf, Status = newStatus };
        await _rolls.SaveAsync(updated, operatorId, ct);
        await _events.NotifyAsync(
            eventCode: "INV_CHG",
            rollId: roll.Id,
            gradeCode: roll.GradeCode,
            newLf: newLf,
            ct: ct);

        return ConsumeRollResult.Ok;
    }
}

public interface IConsumeRollService
{
    Task<ConsumeRollResult> ConsumeAsync(
        string rollId,
        decimal usedLf,
        string operatorId,
        CancellationToken ct = default);
}
