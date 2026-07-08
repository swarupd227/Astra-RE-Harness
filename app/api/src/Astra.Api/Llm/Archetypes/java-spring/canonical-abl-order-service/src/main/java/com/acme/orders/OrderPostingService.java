// SPDX-Spec: openedge/post-batch.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/**
 * Java 21 projection of ABL {@code post-batch.p}. The ABL transaction is
 * BLOCK-scoped — the {@code DO TRANSACTION:} block wraps the whole
 * {@code FOR EACH}, so every {@code Order.Status = "POSTED"} update AND every
 * {@code RUN update-ledger.p} side effect commits or rolls back together, at
 * PER-BATCH granularity (TX-1). If any ledger post fails, an {@code UNDO}
 * reverses the entire block.
 *
 * <p>Because the maven-sidecar builds offline with no real datasource, this
 * scaffold reproduces the all-or-nothing semantics in memory: every order is
 * staged and its ledger entry posted first; only if the WHOLE batch succeeds
 * are the staged POSTED rows written. A failure propagates with nothing saved —
 * exactly the observable behaviour of the ABL block-scoped transaction. On
 * promotion the staging disappears and a single {@code @Transactional} method
 * gives the same guarantee via the datasource.
 */
@TargetMapping(value = "@Service", ablConstruct = "post-batch.p")
public final class OrderPostingService {

    private final OrderRepositoryPort orders;
    private final LedgerPort ledger;
    private final SessionContextPort session;

    public OrderPostingService(OrderRepositoryPort orders, LedgerPort ledger, SessionContextPort session) {
        this.orders = orders;
        this.ledger = ledger;
        this.session = session;
    }

    /**
     * Post every open order in {@code batchId} atomically.
     *
     * @return the number of orders posted (all of them, on success)
     * @throws RuntimeException if any ledger post fails — NOTHING is saved
     *         (TX-1: per-batch rollback)
     */
    @SpecClaim("TX-1")
    @SpecClaim("RP-4")
    @SpecClaim("SV-1")
    @TargetMapping(value = "@Transactional", ablConstruct = "DO TRANSACTION: ... END (per-batch)")
    public int postBatch(int batchId) {
        String user = session.user();          // SV-1: shared-var read, now explicit
        LocalDate date = session.postingDate(); // SV-1

        List<Order> batch = orders.findByBatchForUpdate(batchId); // RP-4
        List<Order> staged = new ArrayList<>(batch.size());

        // Stage every change and post the ledger BEFORE any write. If the
        // ledger throws for one order, we exit here with nothing persisted —
        // the block-scoped rollback (TX-1).
        for (Order o : batch) {
            ledger.post(o.orderNum(), o.total());  // may throw → whole batch undone
            staged.add(o.posted(user, date));      // Order.Status = "POSTED"
        }

        // All ledger posts succeeded → commit the batch as one unit.
        orders.saveAll(staged);
        return staged.size();
    }
}
