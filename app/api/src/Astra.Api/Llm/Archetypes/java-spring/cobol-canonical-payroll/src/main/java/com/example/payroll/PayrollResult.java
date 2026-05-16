package com.example.payroll;

import java.math.BigDecimal;

/**
 * Section-contract output record for the signed COBOL spec (SC-2).
 *
 * The COBOL RESULT-CD numeric mapping ({@code 0=ok, 1=not-found,
 * 2=insufficient, 3=locked}) becomes a strongly-typed
 * {@link Status} enum here. Engineer can still inspect the numeric
 * value via {@link Status#code()} for downstream interop.
 */
public record PayrollResult(
    Status status,
    BigDecimal newAmount,
    int newStatusFlag
) {
    public enum Status {
        OK(0),
        NOT_FOUND(1),
        INSUFFICIENT(2),
        LOCKED(3);

        private final int code;
        Status(int code) { this.code = code; }
        public int code() { return code; }
    }

    public static PayrollResult ok(BigDecimal newAmount, int newStatusFlag) {
        return new PayrollResult(Status.OK, newAmount, newStatusFlag);
    }
    public static PayrollResult notFound() {
        return new PayrollResult(Status.NOT_FOUND, BigDecimal.ZERO, 0);
    }
    public static PayrollResult insufficient() {
        return new PayrollResult(Status.INSUFFICIENT, BigDecimal.ZERO, 0);
    }
    public static PayrollResult locked() {
        return new PayrollResult(Status.LOCKED, BigDecimal.ZERO, 0);
    }
}
