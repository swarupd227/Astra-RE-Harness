// SPDX-Spec: openedge/post-batch.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.math.BigDecimal;
import java.time.LocalDate;

/**
 * Java projection of the ABL {@code Order} buffer as posted by post-batch.p.
 * Immutable; {@link #posted(String, LocalDate)} returns the POSTED copy stamped
 * with the session user/date (the shared-variable reads, SV-1).
 */
@TargetMapping(value = "@Entity Order", ablConstruct = "Order buffer")
public record Order(int orderNum,
                    int batchId,
                    String status,
                    BigDecimal total,
                    String postedBy,
                    LocalDate postedOn) {

    public static final String OPEN = "OPEN";
    public static final String POSTED = "POSTED";

    /** An unposted order in a batch. */
    public static Order open(int orderNum, int batchId, BigDecimal total) {
        return new Order(orderNum, batchId, OPEN, total, null, null);
    }

    /** {@code Order.Status = "POSTED"} + stamp PostedBy/PostedOn from the session. */
    @SpecClaim("SE-1")
    public Order posted(String user, LocalDate date) {
        return new Order(orderNum, batchId, POSTED, total, user, date);
    }
}
