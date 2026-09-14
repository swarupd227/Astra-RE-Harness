using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Roll-stock consumption service derived from a SIGNED spec for
/// CONSUME_ROLL. The Fortran/COBOL claim taxonomy maps to this C# shape:
///   INV-* (invariants)         → behavioural guards in this method
///   SC-* (section contracts)   → method signature + result enum
///   IO-* (I/O side effects)    → IRollRepository + IEventNotifier
///   EC-* (edge cases)          → branch-by-branch tests
///   Q-*  (open questions)      → MUST be resolved before signature
/// </summary>
public sealed class ConsumeRollService
{
    // INV-5 magic constant from the signed spec — surfaced as a named
    // field so the implementation can swap to a per-grade lookup if
    // Q-2 (MIN_REMAIN per-grade?) gets resolved.
    public const decimal MinRemainLf = 12.0m;

    // Roll status sentinel from the source. Q-3 flagged this as a
    // numeric magic; resolution: keep the value, surface as a named
    // const so call sites read intent-first.
    private const int DepletedStatus = 9;

    private readonly IRollRepository _rolls;
    private readonly IEventNotifier _events;

    public ConsumeRollService(IRollRepository rolls, IEventNotifier events)
    {
        _rolls = rolls;
        _events = events;
    }

    /// <summary>
    /// Posts a consumption event for one roll. Translated 1:1 from
    /// CONSUME_ROLL.FOR — behaviour preserved per INV-1..6, EC-1..4.
    /// </summary>
    public async Task<ConsumeRollResult> ConsumeAsync(
        string rollId,
        decimal usedLf,
        string operatorId,
        CancellationToken ct = default)
    {
        // INV-1 / IO-1: ISAM READ on INVMASTR keyed on ROLL_ID.
        // Source returns RESULT_CD=1 when IO_STAT ≠ 0 (not-found).
        var roll = await _rolls.ReadAsync(rollId, ct);
        if (roll is null)
            return ConsumeRollResult.NotFound;

        // INV-2 / EC-2: locked rolls return Locked, no WRITE occurs.
        if (roll.Locked)
            return ConsumeRollResult.Locked;

        // INV-3 / EC-3: USED_LF > ON_HAND_LF returns Insufficient.
        // No clamping — the spec is explicit about a strict-greater
        // comparison and a fail-fast return without persisting.
        if (usedLf > roll.OnHandLf)
            return ConsumeRollResult.Insufficient;

        // INV-4: NEW_LF = ON_HAND_LF − USED_LF.
        // Single REAL subtraction; no clamping (Q-1 noted negative
        // USED_LF would INCREASE stock — preserved as-is pending SME).
        var newLf = roll.OnHandLf - usedLf;

        // INV-5: NEW_LF below threshold marks the roll DEPLETED.
        var newStatus = newLf < MinRemainLf ? DepletedStatus : roll.Status;

        // INV-6 / IO-2: success path persists via ISAM REWRITE then
        // emits the INV_CHG event to downstream consumers.
        var updated = roll with { OnHandLf = newLf, Status = newStatus };
        await _rolls.RewriteAsync(updated, ct);
        await _events.EmitInventoryChangedAsync(
            rollId: roll.Id,
            gradeCd: roll.GradeCd,
            newLf: newLf,
            ct: ct);

        return ConsumeRollResult.Ok;
    }
}

public enum ConsumeRollResult
{
    Ok = 0,
    NotFound = 1,
    Insufficient = 2,
    Locked = 3,
}
