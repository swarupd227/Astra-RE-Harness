// SPDX-Spec: unibasic/ARRAY.PICK (signed)
// SPDX-Archetype: canonical-unibasic-branch-service
package com.acme.branch;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies the LOCATE-then-insert-if-missing idiom (MV-1/MV-2/INV-1). */
class DynamicArrayOpsTests {

    @Test
    @DisplayName("MV-1/INV-1: adds a value that is not yet present")
    void addsWhenAbsent() {
        List<String> branches = new ArrayList<>(List.of("1", "2", "3"));
        boolean added = DynamicArrayOps.addIfAbsent(branches, "12");
        assertThat(added).isTrue();
        assertThat(branches).containsExactly("1", "2", "3", "12");
    }

    @Test
    @DisplayName("MV-2: LOCATE finds an existing value — no duplicate insert (the source's \"already has it\" branch)")
    void noOpWhenAlreadyPresent() {
        List<String> branches = new ArrayList<>(List.of("1", "2", "3"));
        boolean added = DynamicArrayOps.addIfAbsent(branches, "2");
        assertThat(added).isFalse();
        assertThat(branches).containsExactly("1", "2", "3");
    }

    @Test
    @DisplayName("Adding to an empty list works (first branch ever assigned)")
    void addsToEmptyList() {
        List<String> branches = new ArrayList<>();
        boolean added = DynamicArrayOps.addIfAbsent(branches, "PT");
        assertThat(added).isTrue();
        assertThat(branches).containsExactly("PT");
    }
}
