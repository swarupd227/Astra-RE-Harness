// SPDX-Spec: java/PricingReport.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import java.math.BigDecimal;

/**
 * MOD-1: multi-line string concatenation (the Java-11 {@code "line\n" + "line\n"}
 * style) becomes a Java 21 text block with {@code formatted(...)}. Behaviour is
 * unchanged — same characters, same newlines.
 */
@Modernization(value = "text block + formatted()", from = "multi-line string concatenation")
public final class PricingReport {

    @SpecClaim("MOD-1")
    public String render(String sku, int quantity, BigDecimal total) {
        return """
               Work Order Report
               -----------------
               SKU:      %s
               Quantity: %d
               Total:    %s
               """.formatted(sku, quantity, total);
    }
}
