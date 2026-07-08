// SPDX-Spec: openedge/post-batch.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.math.BigDecimal;

/**
 * Port for the ABL {@code RUN update-ledger.p (Order.OrderNum, Order.Total)}
 * external call inside post-batch.p. Because it runs INSIDE the block-scoped
 * transaction, a failure here must roll the whole batch back (TX-1) — so this
 * throws rather than returning a status the caller might ignore.
 */
@TargetMapping(value = "LedgerService bean method (participates in @Transactional)",
               ablConstruct = "RUN update-ledger.p")
public interface LedgerPort {

    /** Post one order's total to the ledger. Throws to force a batch rollback. */
    @SpecClaim("SE-1")
    void post(int orderNum, BigDecimal total);
}
