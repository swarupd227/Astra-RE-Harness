package com.example.rollstock;

/**
 * Roll-stock consumption service derived from the signed spec for
 * CONSUME_ROLL. Method body is stubbed; implementation completes per
 * the cited spec invariants.
 *
 * <p>This is the Spring-flavoured mirror of the .NET 8 archetype's
 * <code>ConsumeRollService.cs</code>. Claim ids (INV-*, SE-*, EC-*)
 * map 1:1 to the signed spec — every TODO names which invariant it
 * unblocks.</p>
 */
public final class ConsumeRollService {

    /** Spec INV-5 magic constant — surfaced as a named field. */
    public static final java.math.BigDecimal MIN_REMAIN_LF = new java.math.BigDecimal("12.0");

    private final RollRepository rolls;
    private final EventNotifier events;

    public ConsumeRollService(RollRepository rolls, EventNotifier events) {
        this.rolls = rolls;
        this.events = events;
    }

    /**
     * TODO: implement per signed spec
     *   INV-1: RESULT_CD=1 (NotFound) when the read yields not-found
     *   INV-2: locked rolls return Locked without modification
     *   INV-3: usedLf > onHandLf returns Insufficient
     *   INV-4: newLf = onHandLf - usedLf (no clamping)
     *   INV-5: newLf < MIN_REMAIN sets rollStatus = 9 (Depleted)
     *   INV-6: success path writes + emits an event
     */
    public ConsumeRollResult consume(
            String rollId,
            java.math.BigDecimal usedLf,
            String operatorId) {
        throw new UnsupportedOperationException(
            "See signed spec INV-1..6 — engineer implementation required.");
    }
}
