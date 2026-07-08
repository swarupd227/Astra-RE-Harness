// SPDX-Spec: java/ShipmentScheduler.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import static org.assertj.core.api.Assertions.assertThat;

import java.time.LocalDate;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** DEP-1/EC-1: LocalDate preserves the legacy Date's calendar day. */
class ShipmentSchedulerTests {

    private final ShipmentScheduler s = new ShipmentScheduler();

    @Test
    @DisplayName("EC-1: dueDate is the same calendar day (no 1900 offset / 0-based month leak)")
    void dueDatePreservesCalendarDay() {
        // Legacy: new Date(2026-1900, 7-1, 7) == 2026-07-07. LocalDate.of(2026,7,7) matches.
        assertThat(s.dueDate(2026, 7, 7)).isEqualTo(LocalDate.of(2026, 7, 7));
    }

    @Test
    @DisplayName("DEP-1: priorityBox uses Integer.valueOf (value-correct)")
    void priorityBoxValue() {
        assertThat(s.priorityBox(5)).isEqualTo(5);
    }
}
