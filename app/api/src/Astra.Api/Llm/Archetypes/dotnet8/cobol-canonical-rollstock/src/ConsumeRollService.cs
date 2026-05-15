using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Roll-stock consumption service derived from a SIGNED COBOL spec for
/// CONSUME-ROLL. The COBOL claim taxonomy maps to this C# shape as:
///   INV-* (invariants)         → behavioural guards in this method
///   SC-* (section contracts)   → method signature + result enum
///   IO-* (I/O side effects)    → IRollRepository + IEventNotifier
///   EC-* (edge cases)          → branch-by-branch tests
///   Q-*  (open questions)      → MUST be resolved before signature
/// Method body is stubbed; implementation completes per the cited
/// invariants in the signed spec.
/// </summary>
public sealed class ConsumeRollService
{
    // INV-5 magic constant — surfaced as a named field so the
    // implementation can swap to a per-grade lookup if Q-2 resolves.
    public const decimal MinRemainLf = 12.0m;

    private readonly IRollRepository _rolls;
    private readonly IEventNotifier _events;

    public ConsumeRollService(IRollRepository rolls, IEventNotifier events)
    {
        _rolls = rolls;
        _events = events;
    }

    public Task<ConsumeRollResult> ConsumeAsync(
        string rollId,
        decimal usedLf,
        string operatorId,
        CancellationToken ct = default)
    {
        // TODO: implement per the signed COBOL spec
        //   INV-1 / IO-1: VSAM READ on ROLL-MASTER; not-found → RESULT_CD=1
        //   INV-2: locked rolls return RESULT_CD=3 without WRITE
        //   INV-3: USED_LF > ON_HAND_LF returns RESULT_CD=2
        //   INV-4: NEW_LF = ON_HAND_LF - USED_LF (no clamping)
        //   INV-5: NEW_LF < MIN_REMAIN sets ROLL_STATUS = 9 (Depleted)
        //   INV-6 / IO-2: success path performs VSAM REWRITE then
        //                 emits INV_CHG via IEventNotifier
        throw new System.NotImplementedException(
            "Engineer-implementation required against the signed COBOL spec.");
    }
}

public enum ConsumeRollResult
{
    Ok = 0,
    NotFound = 1,
    Insufficient = 2,
    Locked = 3,
}
