// SPDX-Spec: php/discount.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.math.BigDecimal;
import java.math.RoundingMode;

/**
 * Java 21 projection of PHP {@code discount.php}'s {@code applyCoupon}. The PHP
 * source used loose {@code ==} comparisons that mis-handle coupon codes:
 * {@code $couponCode == false} is true for the string {@code "0"}, and (pre-PHP-8)
 * {@code $couponCode == 0} is true for ANY non-numeric string. Both silently void
 * a valid coupon.
 *
 * <p>LTC-1: the migration replaces the juggling with an EXPLICIT typed check —
 * a coupon is "absent" only when the code is null or blank, and codes are
 * matched by value ({@code ===} semantics). The string {@code "0"} is a real,
 * non-empty code and is NOT treated as false.
 */
@TargetMapping(value = "@Service", phpConstruct = "discount.php applyCoupon")
public final class CouponService {

    private static final BigDecimal SAVE10_RATE = new BigDecimal("0.10");

    /**
     * @return the discounted subtotal. A null/blank code applies no discount;
     *         "SAVE10" applies 10%; any other non-blank code applies none.
     */
    @SpecClaim("LTC-1")
    @SpecClaim("INV-1")
    public BigDecimal applyCoupon(BigDecimal subtotal, String couponCode) {
        if (subtotal == null) {
            throw new IllegalArgumentException("subtotal is required");
        }
        // LTC-1: "absent" is null-or-blank ONLY — the string "0" is a real code
        // and must NOT be juggled to false the way PHP's == did.
        if (couponCode == null || couponCode.isBlank()) {
            return subtotal;
        }
        // === semantics: match by value/type, not a loose juggle.
        BigDecimal rate = "SAVE10".equals(couponCode) ? SAVE10_RATE : BigDecimal.ZERO;
        BigDecimal discounted = subtotal.subtract(subtotal.multiply(rate));
        return discounted.setScale(2, RoundingMode.HALF_UP);
    }
}
