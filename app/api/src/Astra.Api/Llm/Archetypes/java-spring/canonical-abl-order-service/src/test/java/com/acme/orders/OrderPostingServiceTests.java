// SPDX-Spec: openedge/post-batch.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/**
 * Verifies the block-scoped transaction (TX-1) of post-batch.p: the batch is
 * atomic — every order posts, or (on any ledger failure) NONE do — and each
 * posted order is stamped with the session user/date (SV-1).
 */
class OrderPostingServiceTests {

    private static final LocalDate DAY = LocalDate.of(2026, 7, 6);

    private static final class FakeOrders implements OrderRepositoryPort {
        final List<Order> batch;
        List<Order> saved = null; // stays null unless saveAll is actually called
        FakeOrders(List<Order> batch) { this.batch = batch; }
        @Override public List<Order> findByBatchForUpdate(int batchId) { return batch; }
        @Override public void saveAll(List<Order> orders) { this.saved = new ArrayList<>(orders); }
    }

    private static final class FakeLedger implements LedgerPort {
        final List<Integer> posted = new ArrayList<>();
        final int poisonOrderNum; // -1 = never fails
        FakeLedger(int poisonOrderNum) { this.poisonOrderNum = poisonOrderNum; }
        @Override public void post(int orderNum, BigDecimal total) {
            if (orderNum == poisonOrderNum) {
                throw new IllegalStateException("ledger down for order " + orderNum);
            }
            posted.add(orderNum);
        }
    }

    private static final class FakeSession implements SessionContextPort {
        @Override public String user() { return "acct.poster"; }
        @Override public LocalDate postingDate() { return DAY; }
    }

    private static List<Order> threeOpenOrders() {
        return new ArrayList<>(List.of(
            Order.open(101, 9, new BigDecimal("50.00")),
            Order.open(102, 9, new BigDecimal("75.00")),
            Order.open(103, 9, new BigDecimal("20.00"))));
    }

    @Test
    @DisplayName("TX-1 success: the whole batch posts, and each order is stamped (SV-1)")
    void postBatchAllSuccess() {
        var orders = new FakeOrders(threeOpenOrders());
        var ledger = new FakeLedger(-1);
        var svc = new OrderPostingService(orders, ledger, new FakeSession());

        int count = svc.postBatch(9);

        assertThat(count).isEqualTo(3);
        assertThat(ledger.posted).containsExactly(101, 102, 103);
        assertThat(orders.saved).hasSize(3);
        assertThat(orders.saved).allSatisfy(o -> {
            assertThat(o.status()).isEqualTo(Order.POSTED);
            assertThat(o.postedBy()).isEqualTo("acct.poster");   // SV-1
            assertThat(o.postedOn()).isEqualTo(DAY);              // SV-1
        });
    }

    @Test
    @DisplayName("TX-1 rollback: a ledger failure mid-batch persists NOTHING (per-batch atomicity)")
    void postBatchLedgerFailureRollsBack() {
        var orders = new FakeOrders(threeOpenOrders());
        var ledger = new FakeLedger(102); // fails on the 2nd order
        var svc = new OrderPostingService(orders, ledger, new FakeSession());

        assertThatThrownBy(() -> svc.postBatch(9)).isInstanceOf(IllegalStateException.class);

        assertThat(orders.saved).as("nothing is saved when the batch rolls back").isNull();
        assertThat(ledger.posted).as("only the pre-failure order was attempted").containsExactly(101);
    }

    @Test
    @DisplayName("An empty batch posts zero orders")
    void postBatchEmpty() {
        var orders = new FakeOrders(new ArrayList<>());
        var svc = new OrderPostingService(orders, new FakeLedger(-1), new FakeSession());
        assertThat(svc.postBatch(9)).isZero();
    }
}
