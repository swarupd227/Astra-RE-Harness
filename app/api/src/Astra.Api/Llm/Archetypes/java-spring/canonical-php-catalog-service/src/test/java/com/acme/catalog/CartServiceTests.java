// SPDX-Spec: php/add_to_cart.php, php/cart.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import static org.assertj.core.api.Assertions.assertThat;

import java.math.BigDecimal;
import java.util.LinkedHashMap;
import java.util.Map;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies the session-lifting (SG-1/SE-1) and the cart total (ARR-1/INV-1). */
class CartServiceTests {

    /** In-memory fake of the session superglobal (a request-scoped bean in prod). */
    private static final class FakeSession implements SessionCartPort {
        final Map<String, CartLine> cart = new LinkedHashMap<>();
        @Override public Map<String, CartLine> getCart() { return cart; }
        @Override public void putLine(CartLine line) { cart.put(line.sku(), line); }
    }

    @Test
    @DisplayName("SG-1/SE-1: adding a new sku writes it to the session cart")
    void addNewLine() {
        var session = new FakeSession();
        var svc = new CartService(session);

        int qty = svc.addToCart("WIDGET", 2, new BigDecimal("5.00"));

        assertThat(qty).isEqualTo(2);
        assertThat(session.cart).containsKey("WIDGET");
        assertThat(session.cart.get("WIDGET").qty()).isEqualTo(2);
    }

    @Test
    @DisplayName("Adding an existing sku merges the quantity (the ?? 0 + qty)")
    void mergeExisting() {
        var session = new FakeSession();
        session.cart.put("WIDGET", new CartLine("WIDGET", 2, new BigDecimal("5.00")));
        var svc = new CartService(session);

        int qty = svc.addToCart("WIDGET", 3, new BigDecimal("5.00"));

        assertThat(qty).isEqualTo(5);
        assertThat(session.cart.get("WIDGET").qty()).isEqualTo(5);
    }

    @Test
    @DisplayName("ARR-1/INV-1: cartTotal sums qty × price across all lines, as exact money")
    void cartTotalSums() {
        var session = new FakeSession();
        session.cart.put("A", new CartLine("A", 2, new BigDecimal("5.00")));   // 10.00
        session.cart.put("B", new CartLine("B", 3, new BigDecimal("1.50")));   //  4.50
        var svc = new CartService(session);

        assertThat(svc.cartTotal()).isEqualByComparingTo("14.50");
    }
}
