// SPDX-Spec: php/invoice.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.math.BigDecimal;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies money-as-decimal (EC-1), the typed zero check (LTC-1), and the total (INV-1). */
class MoneyMathTests {

    @Test
    @DisplayName("EC-1: decimal money is exact — 0.10 × 3 == 0.30 (a float would drift)")
    void decimalIsExact() {
        assertThat(MoneyMath.lineTotal(new BigDecimal("0.10"), 3))
            .isEqualByComparingTo("0.30");
    }

    @Test
    @DisplayName("INV-1: total = price × qty, rounded HALF_UP to 2 dp")
    void roundsToTwoPlaces() {
        assertThat(MoneyMath.lineTotal(new BigDecimal("2.005"), 1))
            .isEqualByComparingTo("2.01");
    }

    @Test
    @DisplayName("LTC-1: a zero total is detected by typed compareTo, returned as 0.00")
    void zeroTotal() {
        assertThat(MoneyMath.lineTotal(new BigDecimal("9.99"), 0))
            .isEqualByComparingTo("0.00");
    }

    @Test
    @DisplayName("A negative quantity is rejected")
    void negativeQtyRejected() {
        assertThatThrownBy(() -> MoneyMath.lineTotal(new BigDecimal("1.00"), -1))
            .isInstanceOf(IllegalArgumentException.class);
    }
}
