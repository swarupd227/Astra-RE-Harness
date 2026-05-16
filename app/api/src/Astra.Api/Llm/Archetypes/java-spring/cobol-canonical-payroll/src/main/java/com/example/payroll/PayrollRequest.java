package com.example.payroll;

import java.math.BigDecimal;

/**
 * Section-contract input record for the signed COBOL spec (SC-1).
 *
 * Mirrors the COBOL working-storage / linkage layout the program reads.
 * Generated from the signed spec — field names match the COBOL
 * identifiers (with COBOL hyphens converted to camelCase), and types
 * preserve COBOL numeric semantics (CHAR*n → String, PIC 9(n)V99 →
 * BigDecimal).
 */
public record PayrollRequest(
    String recordId,
    BigDecimal requestedAmount,
    String operatorId
) {}
