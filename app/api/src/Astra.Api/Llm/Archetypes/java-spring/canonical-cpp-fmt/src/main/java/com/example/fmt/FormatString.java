// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// Compile-time-checked-in-source, runtime-validated-in-Java format string
// parser. The C++ source uses constexpr to validate at compile time; Java's
// runtime validation surface is a static factory method.

package com.example.fmt;

import java.util.List;

/** Token kinds emitted by {@link FormatString#parse}. */
enum FormatTokenKind {
    LITERAL,
    PLACEHOLDER,
    ESCAPED_BRACE
}

/** Per-placeholder format spec: width, precision, type-letter, fill char. */
record FormatSpec(int width, int precision, char typeLetter, char fill) {}

record FormatToken(FormatTokenKind kind, String text, FormatSpec spec) {
    public FormatToken(FormatTokenKind kind, String text) {
        this(kind, text, null);
    }
}

/**
 * Parsed format-string. {@code tokens()} is the literal-and-placeholder
 * sequence; {@code placeholderCount()} is the number of arguments the
 * format string expects.
 */
public record FormatString(List<FormatToken> tokens, int placeholderCount) {

    /**
     * Parse a format string into a token sequence.
     *
     * INV-3: a balanced <code>{{</code> / <code>}}</code> pair emits ONE
     * literal <code>{</code> or <code>}</code> token; EC-2 covers the
     * unbalanced-brace case which throws {@link FmtFormatException}.
     */
    public static FormatString parse(String format) {
        if (format == null) throw new NullPointerException("format");
        // TODO(impl): walk `format` char-by-char, accumulating literal runs
        // and lifting `{...}` placeholders into FormatToken(PLACEHOLDER, ...,
        // spec). Handle `{{` and `}}` as escapes. Throw FmtFormatException
        // with the offending index when a `{` has no matching `}`.
        throw new UnsupportedOperationException(
            "FormatString.parse body — fill from signed spec INV-3, EC-2.");
    }
}
