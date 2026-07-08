// SPDX-Spec: openedge/build-order-lines.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.math.BigDecimal;

/**
 * Java 21 projection of the ABL {@code DEFINE TEMP-TABLE ttOrderLine} working
 * set (TT-1). The temp-table is a process-scoped, in-memory relational table
 * with a primary index on (OrderNum, ItemNum); its row projects to this
 * immutable record, and the table itself to a {@code List<OrderLine>} the
 * caller passes around (the ABL {@code OUTPUT PARAMETER TABLE} coupling).
 *
 * <p>If a downstream procedure queried the temp-table relationally (WHERE on
 * the index), the promotion target would instead be a small in-memory H2 table
 * via Spring Data — see {@link TargetMapping} below.
 */
@SpecClaim("TT-1")
@TargetMapping(value = "List<OrderLine> (record); in-memory H2 @Entity if queried by index",
               ablConstruct = "DEFINE TEMP-TABLE ttOrderLine ... INDEX idxOrder OrderNum ItemNum")
public record OrderLine(int orderNum, String itemNum, int qty, BigDecimal lineAmount) {

    /** LineAmt = Qty * Price (INV-1 on build-order-lines.p). */
    @SpecClaim("INV-1")
    public static OrderLine of(int orderNum, String itemNum, int qty, BigDecimal unitPrice) {
        return new OrderLine(orderNum, itemNum, qty, unitPrice.multiply(BigDecimal.valueOf(qty)));
    }
}
