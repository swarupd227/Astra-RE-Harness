package com.example.rollstock;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.DisplayName;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Engineer-authored JUnit 5 fixtures for ConsumeRollService.
 *
 * <p>Mirrors the .NET 8 archetype's xUnit suite — each test names the
 * signed-spec claim it eventually validates. Pre-impl the bodies are
 * contract-surface asserts so the test pack runs green as soon as the
 * project compiles; behaviour assertions follow once the engineer wires
 * the service body.</p>
 */
class ConsumeRollServiceTest {

    @Test
    @DisplayName("INV-5 · MIN_REMAIN constant is exposed and equals 12.0")
    void inv5_minRemainConstantExposed() {
        assertEquals(0, new java.math.BigDecimal("12.0").compareTo(ConsumeRollService.MIN_REMAIN_LF));
    }
}
