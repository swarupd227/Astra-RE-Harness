// SPDX-Spec: java/WorkOrder.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** MOD-1/INV-1: the pattern-switch state machine preserves the legal transitions. */
class WorkOrderStateMachineTests {

    private final WorkOrderStateMachine sm = new WorkOrderStateMachine();

    @Test
    @DisplayName("INV-1: Open → InProgress on start, carrying the operator")
    void startFromOpen() {
        var next = sm.start(new WorkOrderState.Open(), "alice");
        assertThat(next).isInstanceOf(WorkOrderState.InProgress.class);
        assertThat(((WorkOrderState.InProgress) next).operator()).isEqualTo("alice");
    }

    @Test
    @DisplayName("INV-1: InProgress → Completed on complete, carrying the unit count")
    void completeFromInProgress() {
        var done = sm.complete(new WorkOrderState.InProgress("bob"), 42);
        assertThat(done).isInstanceOf(WorkOrderState.Completed.class);
        assertThat(((WorkOrderState.Completed) done).unitsMade()).isEqualTo(42);
    }

    @Test
    @DisplayName("INV-1: illegal transitions throw (cannot start an already-started order)")
    void illegalTransitionThrows() {
        assertThatThrownBy(() -> sm.start(new WorkOrderState.InProgress("bob"), "carol"))
            .isInstanceOf(IllegalStateException.class);
        assertThatThrownBy(() -> sm.complete(new WorkOrderState.Open(), 1))
            .isInstanceOf(IllegalStateException.class);
    }

    @Test
    @DisplayName("MOD-1: the exhaustive switch describes every sealed state")
    void describeAllStates() {
        assertThat(sm.describe(new WorkOrderState.Open())).isEqualTo("open");
        assertThat(sm.describe(new WorkOrderState.InProgress("dan"))).contains("dan");
        assertThat(sm.describe(new WorkOrderState.Completed(7))).contains("7");
        assertThat(sm.describe(new WorkOrderState.Cancelled("scrap"))).contains("scrap");
    }
}
