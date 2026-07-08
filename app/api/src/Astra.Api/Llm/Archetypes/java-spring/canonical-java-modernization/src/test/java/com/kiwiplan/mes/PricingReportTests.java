// SPDX-Spec: java/PricingReport.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import static org.assertj.core.api.Assertions.assertThat;

import java.math.BigDecimal;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** MOD-1: the text block renders the same content as the old concatenation. */
class PricingReportTests {

    @Test
    @DisplayName("MOD-1: the report contains the sku, quantity and total, with a header")
    void rendersReport() {
        String out = new PricingReport().render("WIDGET", 3, new BigDecimal("29.97"));
        assertThat(out)
            .contains("Work Order Report")
            .contains("SKU:      WIDGET")
            .contains("Quantity: 3")
            .contains("Total:    29.97");
        assertThat(out).endsWith("\n"); // text block preserves the trailing newline
    }
}
