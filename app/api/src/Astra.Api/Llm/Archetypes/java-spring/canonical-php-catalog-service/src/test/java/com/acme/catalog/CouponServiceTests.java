// SPDX-Spec: php/discount.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import static org.assertj.core.api.Assertions.assertThat;

import java.math.BigDecimal;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies the loose-== fix (LTC-1) and the discount invariant (INV-1). */
class CouponServiceTests {

    private final CouponService svc = new CouponService();

    @Test
    @DisplayName("INV-1: SAVE10 reduces the subtotal by 10%")
    void save10Applies() {
        assertThat(svc.applyCoupon(new BigDecimal("100"), "SAVE10"))
            .isEqualByComparingTo("90.00");
    }

    @Test
    @DisplayName("LTC-1: the string \"0\" is a real code, NOT juggled to false (the PHP bug)")
    void zeroStringIsNotFalse() {
        // In PHP, ("0" == false) is true, so this coupon voided the discount.
        // Here "0" is a non-blank, unknown code → no discount, subtotal intact,
        // and critically it does NOT crash or get treated as "absent".
        assertThat(svc.applyCoupon(new BigDecimal("100"), "0"))
            .isEqualByComparingTo("100");
    }

    @Test
    @DisplayName("A null or blank code applies no discount")
    void blankCodeNoDiscount() {
        assertThat(svc.applyCoupon(new BigDecimal("100"), null)).isEqualByComparingTo("100");
        assertThat(svc.applyCoupon(new BigDecimal("100"), "   ")).isEqualByComparingTo("100");
    }

    @Test
    @DisplayName("An unknown non-blank code applies no discount (=== semantics, no juggling)")
    void unknownCodeNoDiscount() {
        assertThat(svc.applyCoupon(new BigDecimal("100"), "BOGUS")).isEqualByComparingTo("100");
    }
}
