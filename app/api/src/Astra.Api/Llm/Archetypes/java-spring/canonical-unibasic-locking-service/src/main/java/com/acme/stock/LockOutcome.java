// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/**
 * The three distinct outcomes of {@code READU QTY.REC FROM STK, ITEM.ID
 * LOCKED ... END ELSE ...} (RA-1):
 * <pre>
 *   READU ... LOCKED        -&gt; found, and this caller now holds the lock
 *   the LOCKED clause fires -&gt; another process already holds the lock
 *   the ELSE clause fires   -&gt; no record exists for this key
 * </pre>
 * A boolean or a nullable {@code StockRecord} would collapse "locked by
 * someone else" and "does not exist" into the same falsy case; the source
 * treats them as different conditions with different messages (lines 8 and
 * 11), so the sealed type keeps every caller honest about handling all
 * three.
 */
public sealed interface LockOutcome {

    @SpecClaim("RA-1")
    record Found(StockRecord record) implements LockOutcome {
    }

    @SpecClaim("RA-1")
    record LockedByAnotherUser() implements LockOutcome {
    }

    record NotFound() implements LockOutcome {
    }
}
