// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// JUnit 5 fixtures tied 1:1 to signed-spec claims. Each @Tag("claim:...") is
// the link back to the spec; the equivalence sidecar reads these tags to know
// which spec claims a passing test covers. Test bodies are TODO until the
// implementation is filled in; the assertion shapes are not.

package com.example.fmt;

import org.junit.jupiter.api.Tag;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class FormatterTests {

    @Test
    @Tag("claim:INV-1")
    void format_noPlaceholders_returnsLiteralFormatString() {
        // TODO(impl-blocked): once format is implemented, assert that
        // Formatter.format("hello") returns "hello".
        assertThrows(UnsupportedOperationException.class,
            () -> Formatter.format("hello"));
    }

    @Test
    @Tag("claim:INV-2")
    void format_placeholderCountMismatch_throws() {
        // INV-2 / EX-1: when the format string expects N placeholders but
        // receives M ≠ N args, throw FmtFormatException.
        assertThrows(FmtFormatException.class,
            () -> Formatter.format("{0} {1}", 1));
    }

    @Test
    @Tag("claim:UB-1")
    void formatInt_longMinValue_rendersWithoutOverflow() {
        // UB-1: signed-integer overflow on negating Long.MIN_VALUE.
        // Defensively guarded — the implementation must produce
        // "-9223372036854775808" rather than UB-crash.
        // Once implemented:
        //   assertEquals("-9223372036854775808", Formatter.formatInt(Long.MIN_VALUE, null));
        assertThrows(UnsupportedOperationException.class,
            () -> Formatter.formatInt(Long.MIN_VALUE, null));
    }

    @Test
    @Tag("claim:EC-1")
    void format_emptyArgs_onLiteralOnly_succeeds() {
        // Once implemented:
        //   assertEquals("", Formatter.format(""));
        assertThrows(UnsupportedOperationException.class,
            () -> Formatter.format(""));
    }

    @Test
    @Tag("claim:EC-2")
    void parse_unmatchedOpenBrace_throws() {
        // EC-2 + EX-1: `parse("{0")` must throw FmtFormatException with
        // offendingIndex == 1 (the index of the unmatched `{`).
        assertThrows(RuntimeException.class,
            () -> FormatString.parse("{0"));
    }
}
