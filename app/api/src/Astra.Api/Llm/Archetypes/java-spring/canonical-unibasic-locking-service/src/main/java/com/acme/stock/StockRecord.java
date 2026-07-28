// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/**
 * Projection of QTY.REC, a single-attribute record whose value-1 (angle-
 * bracket position 1, {@code QTY.REC<1>}) holds the on-hand quantity
 * (INV-1). The source never names this field beyond its position; "onHand"
 * is a readable label for this feasibility slice, not a DICT-confirmed name.
 */
public record StockRecord(int onHand) {
}
