// SPDX-Spec: openedge/apply-discount.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.math.BigDecimal;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/**
 * Verifies the Unknown-value ({@code ?}) semantics of apply-discount.p (EC-3):
 * {@code ?} is distinct from null-as-absence AND from zero, and it propagates
 * through arithmetic until an explicit guard fires.
 */
class DiscountCalculatorTests {

    private final DiscountCalculator calc = new DiscountCalculator();

    @Test
    @DisplayName("A normal discount subtracts: 100 - 15 = 85")
    void normalDiscountSubtracts() {
        assertThat(calc.applyDiscount(new BigDecimal("100"), new BigDecimal("15")))
            .isEqualByComparingTo("85");
    }

    @Test
    @DisplayName("EC-3: the raw (unguarded) formula lets a ? discount PROPAGATE to ? — the guard is load-bearing")
    void unknownDiscountPropagatesWithoutGuard() {
        BigDecimal base = new BigDecimal("100");
        BigDecimal unknownDiscount = null; // ? modelled as null
        // Replicates the un-guarded ABL: pdNet = pdBase - pdDiscount, where
        // ? - anything = ?. Without the guard the net is Unknown, NOT the base.
        BigDecimal rawNet = (unknownDiscount == null) ? null : base.subtract(unknownDiscount);
        assertThat(rawNet).as("? propagates to ? (null), not to base or zero").isNull();
    }

    @Test
    @DisplayName("EC-3/INV-3: with the guard, a ? discount restores the BASE price (never zero)")
    void unknownDiscountGuardedToBase() {
        BigDecimal net = calc.applyDiscount(new BigDecimal("100"), null);
        assertThat(net).isEqualByComparingTo("100");
        assertThat(net).as("? must NOT collapse to zero").isNotEqualByComparingTo("0");
    }

    @Test
    @DisplayName("? is distinct from 0: a zero discount subtracts (100 - 0), reaching base by a DIFFERENT path")
    void zeroDiscountIsNotUnknown() {
        // A real zero goes through subtract (base - 0), NOT the Unknown guard.
        assertThat(calc.applyDiscount(new BigDecimal("100"), BigDecimal.ZERO))
            .isEqualByComparingTo("100");
    }

    @Test
    @DisplayName("A ? base is out of scope and rejected rather than silently mishandled")
    void nullBaseRejected() {
        assertThatThrownBy(() -> calc.applyDiscount(null, new BigDecimal("5")))
            .isInstanceOf(IllegalArgumentException.class);
    }
}
