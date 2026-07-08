// SPDX-Spec: php/invoice.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.math.BigDecimal;
import java.math.RoundingMode;

/**
 * Java 21 projection of PHP {@code invoice.php}'s {@code lineTotal}.
 * The PHP source computed money in a float and compared it with a loose
 * {@code $total == "0"}; both are traps this target closes:
 *
 * <ul>
 *   <li>EC-1: money is a {@link BigDecimal} (never a double) so 0.1 + 0.2 == 0.3
 *       holds; the result is rounded HALF_UP to 2 decimal places.</li>
 *   <li>LTC-1: the loose {@code == "0"} becomes an explicit
 *       {@link BigDecimal#compareTo} against zero, not a string juggle.</li>
 * </ul>
 */
public final class MoneyMath {

    private MoneyMath() {}

    /** {@code $total = round($price * $qty, 2)} — as exact decimal money. */
    @SpecClaim("EC-1")
    @SpecClaim("INV-1")
    @SpecClaim("LTC-1")
    @TargetMapping(value = "BigDecimal arithmetic, HALF_UP scale 2",
                   phpConstruct = "round($price * $qty, 2) with float money")
    public static BigDecimal lineTotal(BigDecimal price, int qty) {
        if (price == null) {
            throw new IllegalArgumentException("price is required");
        }
        if (qty < 0) {
            throw new IllegalArgumentException("qty must be non-negative");
        }
        BigDecimal total = price.multiply(BigDecimal.valueOf(qty));
        // LTC-1: the PHP `if ($total == "0")` becomes a typed compareTo, not a
        // string juggle. (compareTo, not equals — 0 vs 0.00 must be equal.)
        if (total.compareTo(BigDecimal.ZERO) == 0) {
            return BigDecimal.ZERO.setScale(2, RoundingMode.HALF_UP);
        }
        return total.setScale(2, RoundingMode.HALF_UP);
    }
}
