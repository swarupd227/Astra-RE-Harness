package com.example.payroll;

import java.util.Optional;

/**
 * Persistence boundary for the signed COBOL spec (IO-1).
 *
 * Maps the COBOL VSAM READ / REWRITE / DELETE verbs to a Java Spring
 * repository surface. Production implementations swap in
 * {@code @Repository}-annotated JDBC, JPA, or VSAM-bridge adapters per
 * deployment; the contract here is the same.
 */
public interface IPayrollRepository {

    /** COBOL VSAM READ keyed on {@code recordId}. Returns empty on AT END / INVALID KEY. */
    Optional<PayrollRecord> readById(String recordId);

    /** COBOL VSAM REWRITE — persist the updated record. */
    void rewrite(PayrollRecord record);

    /**
     * One row of the keyed file. Schema mirrors the COBOL record
     * layout one-for-one — only types translate (CHAR*n → String,
     * PIC 9(n)V99 → BigDecimal).
     */
    record PayrollRecord(
        String id,
        java.math.BigDecimal onHandAmount,
        int statusFlag,
        String gradeCd,
        boolean locked
    ) {}
}
