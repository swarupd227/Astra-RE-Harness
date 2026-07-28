// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies the three-way lock outcome + strictly-less-than stock guard (RA-1/INV-1). */
class StockReservationServiceTests {

    /** In-memory fake of the STOCK file, with a settable lock-holder per item. */
    private static final class FakePort implements StockRecordPort {
        final Map<String, StockRecord> records = new HashMap<>();
        final Map<String, Boolean> lockedByOther = new HashMap<>();
        String lastSavedItemId;
        StockRecord lastSaved;

        @Override
        public LockOutcome tryLockForUpdate(String itemId) {
            if (lockedByOther.getOrDefault(itemId, false)) {
                return new LockOutcome.LockedByAnotherUser();
            }
            StockRecord r = records.get(itemId);
            return r == null ? new LockOutcome.NotFound() : new LockOutcome.Found(r);
        }

        @Override
        public void save(String itemId, StockRecord updated) {
            records.put(itemId, updated);
            lastSavedItemId = itemId;
            lastSaved = updated;
        }
    }

    @Test
    @DisplayName("RA-1/INV-1: reserves and decrements on-hand when stock is sufficient")
    void reservesWhenSufficientStock() {
        var port = new FakePort();
        port.records.put("WIDGET-1", new StockRecord(10));
        var svc = new StockReservationService(port);

        svc.reserve("WIDGET-1", 3);

        assertThat(port.lastSavedItemId).isEqualTo("WIDGET-1");
        assertThat(port.lastSaved.onHand()).isEqualTo(7);
    }

    @Test
    @DisplayName("INV-1: requesting exactly on-hand quantity succeeds, leaving zero (strictly-less-than guard)")
    void exactMatchSucceedsAtZero() {
        var port = new FakePort();
        port.records.put("WIDGET-1", new StockRecord(5));
        var svc = new StockReservationService(port);

        svc.reserve("WIDGET-1", 5);

        assertThat(port.lastSaved.onHand()).isZero();
    }

    @Test
    @DisplayName("INV-1: requesting more than on-hand throws InsufficientStockException, no write occurs")
    void insufficientStockThrows() {
        var port = new FakePort();
        port.records.put("WIDGET-1", new StockRecord(2));
        var svc = new StockReservationService(port);

        assertThatThrownBy(() -> svc.reserve("WIDGET-1", 3))
                .isInstanceOf(InsufficientStockException.class);
        assertThat(port.lastSavedItemId).isNull();
    }

    @Test
    @DisplayName("RA-1: item locked by another user throws ItemLockedException, no write occurs")
    void lockedByAnotherUserThrows() {
        var port = new FakePort();
        port.records.put("WIDGET-1", new StockRecord(10));
        port.lockedByOther.put("WIDGET-1", true);
        var svc = new StockReservationService(port);

        assertThatThrownBy(() -> svc.reserve("WIDGET-1", 1))
                .isInstanceOf(ItemLockedException.class);
        assertThat(port.lastSavedItemId).isNull();
    }

    @Test
    @DisplayName("RA-1: unknown item id throws ItemNotFoundException, no write occurs")
    void unknownItemThrowsNotFound() {
        var port = new FakePort();
        var svc = new StockReservationService(port);

        assertThatThrownBy(() -> svc.reserve("GHOST-ITEM", 1))
                .isInstanceOf(ItemNotFoundException.class);
        assertThat(port.lastSavedItemId).isNull();
    }
}
