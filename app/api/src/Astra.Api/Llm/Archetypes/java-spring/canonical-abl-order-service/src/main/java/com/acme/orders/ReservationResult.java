// SPDX-Spec: openedge/reserve-stock.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

/**
 * Typed projection of the ABL {@code RETURN "<code>"} character results from
 * reserve-stock.p. ABL returned bare strings ("ITEM-NOT-FOUND", "SHORT", or ""
 * for success); Java uses an enum so callers can switch exhaustively.
 */
@SpecClaim("EH-1")
public enum ReservationResult {
    /** Reservation succeeded; on-hand was decremented. */
    OK,
    /** FIND left the buffer NOT AVAILABLE — no such item (RP-3b). */
    ITEM_NOT_FOUND,
    /** On-hand was insufficient for the requested quantity (INV-2). */
    SHORT
}
