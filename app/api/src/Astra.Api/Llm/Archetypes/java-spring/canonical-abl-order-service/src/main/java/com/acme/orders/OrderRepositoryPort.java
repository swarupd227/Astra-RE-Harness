// SPDX-Spec: openedge/post-batch.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.util.List;

/**
 * Port for the ABL {@code FOR EACH Order WHERE Order.BatchId = piBatch
 * EXCLUSIVE-LOCK} set-oriented read plus the batch write. The FOR EACH becomes
 * a query returning a {@code List<Order>}; the EXCLUSIVE-LOCK becomes a
 * write-transaction managed set on promotion.
 */
@TargetMapping(value = "JpaRepository<Order,Integer>",
               ablConstruct = "FOR EACH Order ... EXCLUSIVE-LOCK")
public interface OrderRepositoryPort {

    /** {@code FOR EACH Order WHERE BatchId = ? EXCLUSIVE-LOCK} (RP). */
    @SpecClaim("RP-4")
    @TargetMapping(value = "@Lock(PESSIMISTIC_WRITE) List<Order> findByBatchId(int)",
                   ablConstruct = "FOR EACH Order ... EXCLUSIVE-LOCK")
    List<Order> findByBatchForUpdate(int batchId);

    /** Persist every order in the batch (one flush inside the transaction). */
    @SpecClaim("SE-1")
    void saveAll(List<Order> orders);
}
