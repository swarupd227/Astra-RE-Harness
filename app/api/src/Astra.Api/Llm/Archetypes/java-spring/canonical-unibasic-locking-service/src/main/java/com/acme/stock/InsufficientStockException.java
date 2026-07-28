// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/** Thrown when {@code IF ON.HAND < REQUESTED THEN} fires (INV-1). Strictly
    less-than: a requested quantity exactly equal to on-hand is allowed and
    reduces on-hand to zero — the source does not require on-hand to stay
    strictly positive. */
public final class InsufficientStockException extends RuntimeException {
    public InsufficientStockException(String itemId, int onHand, int requested) {
        super("Insufficient stock for " + itemId + ": on-hand=" + onHand + ", requested=" + requested);
    }
}
