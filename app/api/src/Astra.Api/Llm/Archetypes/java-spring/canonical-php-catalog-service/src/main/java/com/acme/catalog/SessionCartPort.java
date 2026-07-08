// SPDX-Spec: php/add_to_cart.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.util.Map;

/**
 * Explicit projection of the PHP {@code $_SESSION['cart']} superglobal (SG-1). In
 * PHP the cart lived in ambient session state, read and written with no
 * parameter passing — hidden global coupling with no Java equivalent, and a
 * testability + security trap. The migration lifts it to an INJECTED boundary:
 * on promotion a request-scoped {@code @Component} backed by the servlet session;
 * here, an interface a fake drives in tests.
 */
@SpecClaim("SG-1")
@TargetMapping(value = "@Component @RequestScope backed by HttpSession",
               phpConstruct = "$_SESSION['cart']")
public interface SessionCartPort {

    /** Current cart, keyed by sku (⟵ $_SESSION['cart']). Never null; empty if unset. */
    Map<String, CartLine> getCart();

    /** Upsert a line back into the session cart (⟵ $_SESSION['cart'][$sku] = ...). */
    @SpecClaim("SE-1")
    void putLine(CartLine line);
}
