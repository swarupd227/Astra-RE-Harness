// SPDX-Spec: php/add_to_cart.php, php/cart.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.util.Map;

/**
 * Java 21 projection of PHP {@code add_to_cart.php} + {@code cart.php}. The cart
 * (a PHP associative array, ARR-1) is read from and written back to the session
 * via {@link SessionCartPort} (SG-1), instead of touching {@code $_SESSION}
 * directly. Adding to the cart is a real side effect (SE-1); the total is exact
 * decimal money (INV-1).
 */
@TargetMapping(value = "@Service", phpConstruct = "add_to_cart.php / cart.php")
public final class CartService {

    private final SessionCartPort session;

    public CartService(SessionCartPort session) {
        this.session = session;
    }

    /**
     * {@code $_SESSION['cart'][$sku] = ($existing ?? 0) + $qty} — merge a line
     * into the session cart and return the new quantity for that sku.
     */
    @SpecClaim("SG-1")
    @SpecClaim("SE-1")
    public int addToCart(String sku, int qty, BigDecimal price) {
        if (qty < 0) {
            throw new IllegalArgumentException("qty must be non-negative");
        }
        CartLine existing = session.getCart().get(sku); // ?? 0 → null-check below
        CartLine merged = (existing == null)
            ? new CartLine(sku, qty, price)
            : existing.addQty(qty);
        session.putLine(merged); // SE-1: write-back to the session
        return merged.qty();
    }

    /** {@code cartTotal($cart)} — Σ (qty × price), as exact decimal money. */
    @SpecClaim("ARR-1")
    @SpecClaim("INV-1")
    public BigDecimal cartTotal() {
        BigDecimal total = BigDecimal.ZERO;
        for (Map.Entry<String, CartLine> e : session.getCart().entrySet()) {
            CartLine line = e.getValue();
            total = total.add(MoneyMath.lineTotal(line.price(), line.qty()));
        }
        return total.setScale(2, RoundingMode.HALF_UP);
    }
}
