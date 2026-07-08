// SPDX-Spec: java/WorkOrder.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

/**
 * MOD-1: the Java-11 work-order status (an {@code int} status code + an enum +
 * scattered validation) becomes a Java 21 SEALED interface with a record per
 * state. Sealing makes the state set closed and exhaustively switchable (see
 * {@link WorkOrderStateMachine}); each record carries exactly the data that
 * state needs.
 */
@SpecClaim("MOD-1")
@Modernization(value = "sealed interface + records", from = "int status code + enum")
public sealed interface WorkOrderState
        permits WorkOrderState.Open,
                WorkOrderState.InProgress,
                WorkOrderState.Completed,
                WorkOrderState.Cancelled {

    record Open() implements WorkOrderState { }

    record InProgress(String operator) implements WorkOrderState { }

    record Completed(int unitsMade) implements WorkOrderState { }

    record Cancelled(String reason) implements WorkOrderState { }
}
