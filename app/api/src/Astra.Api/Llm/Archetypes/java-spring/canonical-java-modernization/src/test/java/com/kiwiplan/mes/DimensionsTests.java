// SPDX-Spec: java/Dimensions.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** MOD-1/EC-1: the record preserves the POJO's area + value-equality semantics. */
class DimensionsTests {

    @Test
    @DisplayName("INV-1: area is width × height")
    void area() {
        assertThat(new Dimensions(4, 5).area()).isEqualTo(20);
    }

    @Test
    @DisplayName("EC-1: the record's generated equals/hashCode cover all components")
    void valueEquality() {
        var a = new Dimensions(4, 5);
        var b = new Dimensions(4, 5);
        assertThat(a).isEqualTo(b);
        assertThat(a.hashCode()).isEqualTo(b.hashCode());
        assertThat(a).isNotEqualTo(new Dimensions(4, 6));
    }
}
