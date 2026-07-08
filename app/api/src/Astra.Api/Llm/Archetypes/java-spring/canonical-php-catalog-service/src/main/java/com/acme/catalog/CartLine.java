// SPDX-Spec: php/cart.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.math.BigDecimal;

/**
 * Java projection of one entry in the PHP cart array (ARR-1). In PHP the cart is
 * a single associative array {@code $cart[$sku] = ['qty'=>int, 'price'=>float]}
 * — one type doing duty as both a map (keyed by sku) and a record (the
 * qty/price shape). The migration resolves the record half to this immutable
 * type; the map half becomes a {@code Map<String,CartLine>} (see
 * {@link SessionCartPort}).
 *
 * <p>Money is a {@link BigDecimal}, never a float/double (EC: PHP money-as-float
 * loses precision).
 */
@SpecClaim("ARR-1")
@TargetMapping(value = "record (the value half of Map<String,CartLine>)",
               phpConstruct = "$cart[$sku] = ['qty'=>int, 'price'=>float]")
public record CartLine(String sku, int qty, BigDecimal price) {

    public CartLine {
        if (sku == null || sku.isBlank()) {
            throw new IllegalArgumentException("sku is required");
        }
        if (qty < 0) {
            throw new IllegalArgumentException("qty must be non-negative");
        }
        if (price == null) {
            throw new IllegalArgumentException("price is required (money is never a null/absent float)");
        }
    }

    /** Returns a copy with the quantity increased by {@code delta} (the cart merge). */
    public CartLine addQty(int delta) {
        return new CartLine(sku, qty + delta, price);
    }
}
