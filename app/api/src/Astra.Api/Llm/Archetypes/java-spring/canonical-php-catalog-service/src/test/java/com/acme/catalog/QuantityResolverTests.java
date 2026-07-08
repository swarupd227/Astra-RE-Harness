// SPDX-Spec: php/qty.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies the null/empty semantics fix (NUL-1) and the (int) cast (EC-1). */
class QuantityResolverTests {

    private final QuantityResolver r = new QuantityResolver();

    private static Map<String, String> input(String qty) {
        var m = new HashMap<String, String>();
        if (qty != null) m.put("qty", qty);
        return m;
    }

    @Test
    @DisplayName("NUL-1: a real \"0\" is PRESERVED, not forced to 1 (the PHP empty(\"0\") bug is fixed)")
    void zeroPreserved() {
        // PHP's empty("0") is true, so qty.php silently rewrote 0 → 1.
        assertThat(r.resolveQty(input("0"))).isEqualTo(0);
    }

    @Test
    @DisplayName("An absent key defaults to 1 (the ?? behaviour)")
    void absentDefaults() {
        assertThat(r.resolveQty(input(null))).isEqualTo(1);
        assertThat(r.resolveQty(new HashMap<>())).isEqualTo(1);
    }

    @Test
    @DisplayName("An explicitly blank string defaults to 1")
    void blankDefaults() {
        assertThat(r.resolveQty(input("   "))).isEqualTo(1);
    }

    @Test
    @DisplayName("A normal quantity parses through")
    void normalParses() {
        assertThat(r.resolveQty(input("5"))).isEqualTo(5);
    }

    @Test
    @DisplayName("EC-1: garbage is rejected (no PHP leading-digit truncation of \"5 apples\")")
    void garbageRejected() {
        assertThatThrownBy(() -> r.resolveQty(input("5 apples")))
            .isInstanceOf(IllegalArgumentException.class);
    }
}
