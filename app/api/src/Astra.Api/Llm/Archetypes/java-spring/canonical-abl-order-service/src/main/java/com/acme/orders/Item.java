// SPDX-Spec: openedge/reserve-stock.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

/**
 * Java projection of the ABL {@code Item} database buffer. In ABL the buffer is
 * an implicit record handle; here it is an immutable record, and a mutation
 * (the {@code Item.OnHand = Item.OnHand - piQty} assignment under the exclusive
 * lock) is modelled as a copy via {@link #withOnHand(int)} that the repository
 * port persists.
 */
@TargetMapping(value = "@Entity Item { @Id String itemNum; int onHand; }",
               ablConstruct = "Item buffer (physical DB table)")
public record Item(String itemNum, int onHand) {

    public Item {
        if (itemNum == null || itemNum.isBlank()) {
            throw new IllegalArgumentException("itemNum is required");
        }
    }

    /** Returns a copy with a new on-hand quantity (the ASSIGN under EXCLUSIVE-LOCK). */
    public Item withOnHand(int newOnHand) {
        return new Item(itemNum, newOnHand);
    }
}
