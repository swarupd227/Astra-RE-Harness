// SPDX-Spec: openedge/reserve-stock.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/**
 * Verifies the signed invariants of reserve-stock.p on the REAL Java logic.
 * There is no source-execution equivalence gate for ABL (the Progress AVM is
 * proprietary), so these example + property-style tests ARE the verification.
 */
class StockReservationServiceTests {

    /** In-memory fake of the repository port (a JpaRepository in production). */
    private static final class FakeItems implements ItemRepositoryPort {
        final Map<String, Item> store = new HashMap<>();

        FakeItems put(Item i) { store.put(i.itemNum(), i); return this; }

        @Override public Optional<Item> findForUpdate(String itemNum) { return Optional.ofNullable(store.get(itemNum)); }
        @Override public Optional<Item> findReadOnly(String itemNum) { return Optional.ofNullable(store.get(itemNum)); }
        @Override public Item save(Item item) { store.put(item.itemNum(), item); return item; }
    }

    @Test
    @DisplayName("RP-3/OK: a valid reservation decrements on-hand under the exclusive lock")
    void reserveSuccessDecrements() {
        var items = new FakeItems().put(new Item("WIDGET", 10));
        var svc = new StockReservationService(items);

        assertThat(svc.reserve("WIDGET", 3)).isEqualTo(ReservationResult.OK);
        assertThat(items.store.get("WIDGET").onHand()).isEqualTo(7);
    }

    @Test
    @DisplayName("INV-2: an insufficient reservation returns SHORT and leaves on-hand untouched")
    void reserveInsufficientReturnsShort() {
        var items = new FakeItems().put(new Item("WIDGET", 2));
        var svc = new StockReservationService(items);

        assertThat(svc.reserve("WIDGET", 5)).isEqualTo(ReservationResult.SHORT);
        assertThat(items.store.get("WIDGET").onHand()).isEqualTo(2); // never oversell
    }

    @Test
    @DisplayName("RP-3b: a FIND with no match (NOT AVAILABLE) returns ITEM_NOT_FOUND")
    void reserveMissingItem() {
        var svc = new StockReservationService(new FakeItems());
        assertThat(svc.reserve("NOPE", 1)).isEqualTo(ReservationResult.ITEM_NOT_FOUND);
    }

    @Test
    @DisplayName("INV-2 (property): on-hand is NEVER driven negative for any requested qty")
    void reserveNeverGoesNegative() {
        for (int qty = 0; qty <= 15; qty++) {
            var items = new FakeItems().put(new Item("WIDGET", 10));
            var svc = new StockReservationService(items);
            svc.reserve("WIDGET", qty);
            assertThat(items.store.get("WIDGET").onHand())
                .as("on-hand after reserving %d from 10", qty)
                .isGreaterThanOrEqualTo(0);
        }
    }

    @Test
    @DisplayName("Guard: a negative quantity is rejected rather than silently adding stock")
    void reserveNegativeQtyRejected() {
        var svc = new StockReservationService(new FakeItems().put(new Item("WIDGET", 10)));
        assertThatThrownBy(() -> svc.reserve("WIDGET", -4))
            .isInstanceOf(IllegalArgumentException.class);
    }
}
