// SPDX-Spec: java/BatchPlanner.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatCode;

import java.util.List;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** EC-1: the modernized code keeps a MUTABLE list, so the toList() trap is avoided. */
class BatchPlannerTests {

    private final BatchPlanner p = new BatchPlanner();

    @Test
    @DisplayName("INV-1: returns SKUs with qty > 10, plus the appended EXPEDITE marker")
    void filtersAndAppends() {
        var items = List.of(
            new WorkItem("A", 5),    // below threshold
            new WorkItem("B", 11),   // above
            new WorkItem("C", 20));  // above
        assertThat(p.highPriority(items)).containsExactly("B", "C", "EXPEDITE");
    }

    @Test
    @DisplayName("EC-1: the returned list is MUTABLE (a naive Stream.toList() would have thrown)")
    void resultIsMutable() {
        var result = p.highPriority(List.of(new WorkItem("B", 11)));
        // The whole point of the edge case: this add must NOT throw.
        assertThatCode(() -> result.add("EXTRA")).doesNotThrowAnyException();
    }
}
