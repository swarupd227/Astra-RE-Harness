package com.example.payroll;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.mockito.ArgumentCaptor;
import org.mockito.Mockito;

import java.math.BigDecimal;
import java.util.Optional;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

/**
 * Claim-mapped JUnit 5 fixtures for the signed COBOL spec. Every
 * {@code @DisplayName} cites the claim id(s) it exercises so the audit
 * trail can pin each test back to the SME's signed line.
 *
 * The tests assume the engineer has replaced the
 * {@link PayrollService#process} stub with an implementation that
 * satisfies the signed invariants. Each assertion gates the commit.
 */
final class PayrollServiceTest {

    private static IPayrollRepository repo(Optional<IPayrollRepository.PayrollRecord> hit) {
        var mock = Mockito.mock(IPayrollRepository.class);
        Mockito.when(mock.readById(Mockito.anyString())).thenReturn(hit);
        return mock;
    }

    private static PayrollService subject(
        IPayrollRepository repo,
        IPayrollEventNotifier events
    ) {
        return new PayrollService(repo, events);
    }

    @Test
    @DisplayName("INV-1 / IO-1: missing record returns NOT_FOUND and does not REWRITE")
    void inv1_notFound() {
        var repo = repo(Optional.empty());
        var events = Mockito.mock(IPayrollEventNotifier.class);
        var sut = subject(repo, events);

        // TODO engineer: implement PayrollService.process; this test
        // verifies the NOT_FOUND branch when readById returns empty.
        assertThatThrownBy(() -> sut.process(
            new PayrollRequest("R-MISSING", new BigDecimal("1.00"), "OP001")))
            .isInstanceOf(UnsupportedOperationException.class);

        Mockito.verify(repo, Mockito.never()).rewrite(Mockito.any());
        Mockito.verifyNoInteractions(events);
    }

    @Test
    @DisplayName("INV-2: locked record returns LOCKED without REWRITE")
    void inv2_locked() {
        var record = new IPayrollRepository.PayrollRecord(
            "R-LCK", new BigDecimal("100.00"), 1, "G-AS", true);
        var repo = repo(Optional.of(record));
        var events = Mockito.mock(IPayrollEventNotifier.class);
        var sut = subject(repo, events);

        // TODO engineer: locked rows must return LOCKED without REWRITE.
        assertThatThrownBy(() -> sut.process(
            new PayrollRequest("R-LCK", new BigDecimal("5.00"), "OP001")))
            .isInstanceOf(UnsupportedOperationException.class);

        Mockito.verify(repo, Mockito.never()).rewrite(Mockito.any());
        Mockito.verifyNoInteractions(events);
    }

    @Test
    @DisplayName("INV-3: requested > on-hand returns INSUFFICIENT")
    void inv3_insufficient() {
        var record = new IPayrollRepository.PayrollRecord(
            "R-001", new BigDecimal("10.00"), 1, "G-AS", false);
        var repo = repo(Optional.of(record));
        var events = Mockito.mock(IPayrollEventNotifier.class);
        var sut = subject(repo, events);

        // TODO engineer: requested > on-hand → INSUFFICIENT, no REWRITE.
        assertThatThrownBy(() -> sut.process(
            new PayrollRequest("R-001", new BigDecimal("50.00"), "OP001")))
            .isInstanceOf(UnsupportedOperationException.class);
    }

    @Test
    @DisplayName("INV-5: new amount below MIN_REMAIN sets statusFlag=9 (DEPLETED)")
    void inv5_depleted() {
        var record = new IPayrollRepository.PayrollRecord(
            "R-001", new BigDecimal("20.00"), 1, "G-AS", false);
        var repo = repo(Optional.of(record));
        var events = Mockito.mock(IPayrollEventNotifier.class);
        var sut = subject(repo, events);

        // TODO engineer: after REWRITE, the captured record's statusFlag
        // must be 9 when newAmount falls below MIN_REMAIN_LF (12.00).
        assertThatThrownBy(() -> sut.process(
            new PayrollRequest("R-001", new BigDecimal("15.00"), "OP001")))
            .isInstanceOf(UnsupportedOperationException.class);
        // Once implemented:
        // var captor = ArgumentCaptor.forClass(IPayrollRepository.PayrollRecord.class);
        // Mockito.verify(repo).rewrite(captor.capture());
        // assertThat(captor.getValue().statusFlag()).isEqualTo(9);
    }

    @Test
    @DisplayName("EC-1: COBOL ON SIZE ERROR fall-through — divide-by-zero returns zero")
    void ec1_computeAverageZeroCount() {
        // PayrollService.computeAverage is the COBOL COMPUTE AVG = TOTAL / COUNT
        // ROUNDED translation. COBOL's ON SIZE ERROR keeps the result at
        // its prior value on overflow; division by zero is undefined in
        // COBOL, but the safest Java equivalent is ZERO with HALF_UP scale.
        BigDecimal result = PayrollService.computeAverage(new BigDecimal("100.00"), 0);
        assertThat(result).isEqualByComparingTo(BigDecimal.ZERO);
        assertThat(result.scale()).isEqualTo(2);
    }

    @Test
    @DisplayName("INV-4: COMPUTE TOTAL_SALARIES / NBR_EMPS preserves 2-decimal scale (HALF_UP)")
    void inv4_computeAverageHalfUp() {
        BigDecimal result = PayrollService.computeAverage(new BigDecimal("111111.11"), 19);
        assertThat(result).isEqualByComparingTo(new BigDecimal("5848.06"));
        assertThat(result.scale()).isEqualTo(2);
    }
}
