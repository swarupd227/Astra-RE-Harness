// SPDX-Spec: java/ShipmentScheduler.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import java.time.LocalDate;

/**
 * DEP-1: replaces deprecated-for-removal APIs. The Java-11 source used
 * {@code new Date(year-1900, month-1, day)} (the notorious 1900 year offset +
 * 0-based month) and {@code new Integer(int)}.
 *
 * <p>EC-1: the calendar semantics are preserved — {@link LocalDate#of} takes the
 * FULL year and a 1-based month, so the caller passes real values and the old
 * {@code -1900}/{@code -1} adjustments disappear (they were an artifact of the
 * legacy Date API, not domain behaviour).
 */
@Modernization(value = "java.time.LocalDate / Integer.valueOf", from = "new Date(y-1900,m-1,d) / new Integer(int)")
public final class ShipmentScheduler {

    /** Build the due date (INV-1: same calendar day as the legacy Date). */
    @SpecClaim("DEP-1")
    @SpecClaim("EC-1")
    @SpecClaim("INV-1")
    public LocalDate dueDate(int year, int month, int day) {
        return LocalDate.of(year, month, day);
    }

    /** DEP-1: Integer.valueOf, not the deprecated boxing constructor. */
    @SpecClaim("DEP-1")
    public Integer priorityBox(int priority) {
        return Integer.valueOf(priority);
    }
}
