// SPDX-Spec: java/Dimensions.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

/**
 * MOD-1: the verbose Java-11 immutable POJO (final class + constructor + getters
 * + hand-written equals/hashCode) collapses to a Java 21 record. The record's
 * generated equals/hashCode cover ALL components — verified equivalent to the
 * hand-written version (EC-1: had the old equals omitted a field, the record
 * would NOT be behaviourally identical).
 */
@SpecClaim("MOD-1")
@SpecClaim("EC-1")
@Modernization(value = "record", from = "final class + getters + equals/hashCode")
public record Dimensions(int width, int height) {

    /** INV-1: area is width × height. */
    @SpecClaim("INV-1")
    public int area() {
        return width * height;
    }
}
