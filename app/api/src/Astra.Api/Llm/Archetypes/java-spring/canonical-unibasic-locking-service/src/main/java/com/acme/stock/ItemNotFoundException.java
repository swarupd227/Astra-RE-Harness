// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/** Thrown when READU's ELSE clause fires (RA-1): no record exists for the
    requested item id. */
public final class ItemNotFoundException extends RuntimeException {
    public ItemNotFoundException(String itemId) {
        super("Item not found: " + itemId);
    }
}
