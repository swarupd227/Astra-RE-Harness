// SPDX-Spec: java/WorkOrder.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

/**
 * MOD-1: the Java-11 if-else / instanceof transition chains become Java 21
 * pattern-matching {@code switch} expressions (JEP 441, final in 21). Switching
 * over the sealed {@link WorkOrderState} is exhaustive, so {@link #describe}
 * needs no default — the compiler proves every state is handled.
 *
 * <p>INV-1: the legal transitions (Open→InProgress→Completed, and Cancelled is
 * terminal) are preserved exactly from the Java-11 state machine.
 */
@Modernization(value = "pattern matching for switch (JEP 441)", from = "if-else / instanceof chain")
public final class WorkOrderStateMachine {

    /** Start work: only an Open order may start. */
    @SpecClaim("MOD-1")
    @SpecClaim("INV-1")
    public WorkOrderState start(WorkOrderState state, String operator) {
        return switch (state) {
            case WorkOrderState.Open ignored -> new WorkOrderState.InProgress(operator);
            default -> throw new IllegalStateException("cannot start from " + describe(state));
        };
    }

    /** Complete work: only an InProgress order may complete. */
    @SpecClaim("INV-1")
    public WorkOrderState complete(WorkOrderState state, int unitsMade) {
        return switch (state) {
            case WorkOrderState.InProgress ignored -> new WorkOrderState.Completed(unitsMade);
            default -> throw new IllegalStateException("can only complete from InProgress, not " + describe(state));
        };
    }

    /** Human-readable label — an exhaustive switch over the sealed type. */
    @SpecClaim("MOD-1")
    public String describe(WorkOrderState state) {
        return switch (state) {
            case WorkOrderState.Open ignored -> "open";
            case WorkOrderState.InProgress ip -> "in progress by " + ip.operator();
            case WorkOrderState.Completed c -> "completed: " + c.unitsMade() + " units";
            case WorkOrderState.Cancelled c -> "cancelled: " + c.reason();
        };
    }
}
