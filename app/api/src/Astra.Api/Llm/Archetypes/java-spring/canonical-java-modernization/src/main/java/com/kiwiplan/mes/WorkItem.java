// SPDX-Spec: java/BatchPlanner.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

/**
 * MOD-1: a small immutable data carrier as a record (was a POJO with getSku()/
 * getQty()). Accessors are {@code sku()} / {@code qty()}.
 */
@SpecClaim("MOD-1")
@Modernization(value = "record", from = "POJO with getSku()/getQty()")
public record WorkItem(String sku, int qty) {
}
