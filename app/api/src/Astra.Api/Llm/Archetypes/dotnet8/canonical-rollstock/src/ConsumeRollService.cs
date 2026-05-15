using System;
using System.Threading;
using System.Threading.Tasks;

namespace Demo.RollStock;

/// <summary>
/// Roll-stock consumption service derived from the signed spec for
/// CONSUME_ROLL. Method bodies are stubbed; implementation completes
/// per the cited spec invariants.
/// </summary>
public sealed class ConsumeRollService : IConsumeRollService
{
    // Spec INV-5 magic constant — surfaced as a named field so the
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
        // TODO: implement per signed spec
        //   INV-1: RESULT_CD=1 (NotFound) when INV_READ yields IO_STAT != 0
        //   INV-2: locked rolls return Locked without modification
        //   INV-3: USED_LF > ON_HAND_LF returns Insufficient
        //   INV-4: NEW_LF = ON_HAND_LF - USED_LF (no clamping)
        //   INV-5: NEW_LF < MIN_REMAIN sets ROLL_STATUS = 9 (Depleted)
        //   INV-6: success path performs INV_WRITE + emits EMIT_EVENT
        throw new NotImplementedException(
            "See signed spec INV-1..6 — engineer implementation required.");
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
