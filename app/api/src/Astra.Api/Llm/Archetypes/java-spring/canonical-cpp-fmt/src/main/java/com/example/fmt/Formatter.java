// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// Java 17 + Spring Boot pure-Java projection of fmt::format. Per ADR-027 this
// is the default variant — no native dependency. Per ADR-026 one spec per
// primary template; Java's varargs `Object... args` collapses the C++
// `template<typename... Args>` pack. The type-erased visitor in fmt becomes
// a plain switch on the argument's runtime class. The implementation BODY
// is intentionally TODO — the scaffold is a contract, not a translation.
// The signed spec is the source of truth; the equivalence sidecar (Phase
// 9.1.f / gpp-sidecar) and the property-test sidecar confirm behavioural
// parity.

package com.example.fmt;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import org.springframework.stereotype.Component;

@Retention(RetentionPolicy.RUNTIME)
@Repeatable(SpecClaim.Container.class)
@interface SpecClaim {
    String value();
    @Retention(RetentionPolicy.RUNTIME)
    @interface Container { SpecClaim[] value(); }
}

/**
 * Java 17 projection of <code>fmt::format</code>. Variadic-args mirror of the
 * primary template; the strongly-typed integral overload mirrors the
 * <code>fmt::detail::format_int</code> fast path.
 *
 * <p>Exception contract (EX-1): basic guarantee. On format-string error throws
 * {@link FmtFormatException}; the partial output is not returned and no state
 * outside the returned string is observable.
 */
@Component
@SpecClaim("TI-1")
@SpecClaim("EX-1")
public class Formatter {

    /**
     * Primary entry point — variadic mirror of
     * <code>template&lt;typename... Args&gt; string format(string_view, Args...)</code>.
     */
    @SpecClaim("INV-1")
    @SpecClaim("INV-2")
    @SpecClaim("OL-1")
    public static String format(String fmtstr, Object... args) {
        if (fmtstr == null) throw new NullPointerException("fmtstr");
        if (args == null) throw new NullPointerException("args");
        var spec = FormatString.parse(fmtstr);
        if (spec.placeholderCount() != args.length) {
            throw new FmtFormatException(
                "format string expects " + spec.placeholderCount() + " args, got " + args.length);
        }
        // TODO(impl): walk spec.tokens(), dispatch each Placeholder to the
        // matching arg via a runtime-type switch, then concatenate via
        // StringBuilder. The C++ source uses a constexpr-evaluated tuple-
        // visit; the Java equivalent is a runtime instanceof / pattern
        // switch (Java 21+ pattern matching is cleaner if available).
        throw new UnsupportedOperationException(
            "format body — fill from signed spec INV-1, INV-2, OL-1.");
    }

    /**
     * Strongly-typed integral overload — mirrors the
     * <code>fmt::detail::format_int</code> fast-path. UB-1 (signed overflow on
     * negating Long.MIN_VALUE) is defensively guarded.
     */
    @SpecClaim("INV-3")
    @SpecClaim("UB-1")
    public static String formatInt(long value, FormatSpec spec) {
        // TODO(impl): write ASCII digits into a fixed-size char buffer;
        // defensively handle Long.MIN_VALUE via unsigned absolute value
        // (per UB-1; same trick as the C++ source).
        throw new UnsupportedOperationException(
            "formatInt body — fill from signed spec INV-3, UB-1.");
    }
}
