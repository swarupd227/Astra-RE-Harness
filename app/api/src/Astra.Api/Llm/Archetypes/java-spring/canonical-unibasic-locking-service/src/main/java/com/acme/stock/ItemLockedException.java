// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/** Thrown when READU's LOCKED clause fires (RA-1): another process already
    holds the exclusive lock. The source fails fast with no retry — this
    exception preserves that as-written behavior; a real target may choose
    to retry with backoff instead (flagged as an open question, Q-1). */
public final class ItemLockedException extends RuntimeException {
    public ItemLockedException(String itemId) {
        super("Item locked by another user, retry later: " + itemId);
    }
}
