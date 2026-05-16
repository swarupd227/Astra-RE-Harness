package com.example.payroll;

import java.math.BigDecimal;

/**
 * Downstream-notification boundary for the signed COBOL spec (IO-2).
 *
 * Maps the COBOL inventory-changed event emission to a Java Spring
 * notifier interface. Implementations can adapt to Kafka, RabbitMQ,
 * AWS EventBridge, or the in-process Spring ApplicationEventPublisher
 * — the service contract stays unchanged.
 */
public interface IPayrollEventNotifier {

    /** Emit the INV-CHG event after a successful REWRITE (IO-2). */
    void emitInventoryChanged(String recordId, String gradeCd, BigDecimal newAmount);
}
