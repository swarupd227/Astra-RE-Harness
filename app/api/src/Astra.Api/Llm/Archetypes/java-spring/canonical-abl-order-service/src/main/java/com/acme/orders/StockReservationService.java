// SPDX-Spec: openedge/reserve-stock.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.util.Optional;

/**
 * Java 21 projection of ABL {@code reserve-stock.p}. Unlike a pure signature
 * scaffold, the business logic here is REAL — the stock arithmetic and the two
 * guard paths are implemented so the JUnit suite verifies the invariants for
 * keeps. Only the data access is abstracted behind {@link ItemRepositoryPort},
 * which a mock/fake drives in tests and a JpaRepository drives in production.
 *
 * <p>ABL source (the whole procedure runs as one implicit transaction because it
 * updates the DB):
 * <pre>
 *   FIND FIRST Item WHERE Item.ItemNum = pcItem EXCLUSIVE-LOCK NO-ERROR.
 *   IF NOT AVAILABLE Item THEN RETURN "ITEM-NOT-FOUND".
 *   IF Item.OnHand &lt; piQty THEN RETURN "SHORT".
 *   Item.OnHand = Item.OnHand - piQty.
 * </pre>
 */
@TargetMapping(value = "@Service", ablConstruct = "reserve-stock.p")
public final class StockReservationService {

    private final ItemRepositoryPort items;

    public StockReservationService(ItemRepositoryPort items) {
        this.items = items;
    }

    /**
     * Reserve {@code qty} units of {@code itemNum}.
     *
     * <p>The whole method is the transaction boundary (TX): the exclusive-locked
     * read and the on-hand decrement commit together. On promotion this carries
     * {@code @Transactional} and the repository read carries
     * {@code @Lock(PESSIMISTIC_WRITE)}.
     *
     * @return {@link ReservationResult#ITEM_NOT_FOUND} if no such item (RP-3b),
     *         {@link ReservationResult#SHORT} if on-hand &lt; qty (INV-2),
     *         otherwise {@link ReservationResult#OK} after decrementing.
     */
    @SpecClaim("RP-3")
    @SpecClaim("RP-3b")
    @SpecClaim("INV-2")
    @TargetMapping(value = "@Transactional", ablConstruct = "implicit transaction over the OnHand update")
    public ReservationResult reserve(String itemNum, int qty) {
        if (qty < 0) {
            // ABL would let a negative piQty ADD stock; we treat it as a caller
            // error rather than silently reproduce that latent bug (an SME
            // confirmed this guard on sign-off).
            throw new IllegalArgumentException("qty must be non-negative");
        }

        // FIND FIRST ... EXCLUSIVE-LOCK  →  findForUpdate; NOT AVAILABLE → empty.
        Optional<Item> found = items.findForUpdate(itemNum);
        if (found.isEmpty()) {
            return ReservationResult.ITEM_NOT_FOUND;   // RP-3b (guarded not-found)
        }

        Item item = found.get();
        if (item.onHand() < qty) {
            return ReservationResult.SHORT;            // INV-2 (never oversell)
        }

        // Item.OnHand = Item.OnHand - piQty  (the ASSIGN under the lock).
        items.save(item.withOnHand(item.onHand() - qty));
        return ReservationResult.OK;
    }
}
