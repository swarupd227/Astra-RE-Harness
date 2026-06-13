// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// Exception type for format-string failures. Per spec claim EX-1 the routine
// gives the BASIC exception guarantee: on throw the partial output is not
// returned, no observable state outside the returned string is mutated.
//
// Named FmtFormatException rather than FormatException because java.util's
// java.util.IllegalFormatException occupies the obvious namespace; shadowing
// it would surprise readers. The corresponding C++ type is fmt::format_error
// (which derives from std::runtime_error); the Java equivalent is unchecked.

package com.example.fmt;

public class FmtFormatException extends RuntimeException {

    /** 0-based character index in the original format string; -1 if unknown. */
    private final int offendingIndex;

    public FmtFormatException(String message) {
        super(message);
        this.offendingIndex = -1;
    }

    public FmtFormatException(String message, int offendingIndex) {
        super(message);
        this.offendingIndex = offendingIndex;
    }

    public int getOffendingIndex() {
        return offendingIndex;
    }
}
