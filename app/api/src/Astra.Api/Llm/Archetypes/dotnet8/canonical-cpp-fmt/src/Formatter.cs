// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// .NET 8 projection of fmt::format. Per ADR-026, one spec per primary template
// (not per instantiation), so the .NET signature surfaces ONE Format method on
// `Formatter` taking `params object?[]` plus typed convenience overloads. The
// type-erased visitor pattern in fmt becomes a plain switch on the argument's
// runtime type. The implementation BODY is intentionally TODO — the scaffold
// is a contract, not a translation. The signed spec is the source of truth;
// the implementer fills the body and the equivalence sidecar (Phase 9.1.f /
// gpp-sidecar) plus the property-test sidecar confirm behavioural parity.

namespace Demo.Fmt;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>
/// .NET 8 projection of <c>fmt::format</c>. Takes a compile-time-checked format
/// string and a variadic pack of arguments, returns the formatted string.
/// </summary>
/// <remarks>
/// Exception contract (EX-1): basic guarantee. On format-string error throws
/// <see cref="FormatException"/>; the partial output is not returned and no
/// state outside the returned string is observable.
/// </remarks>
[SpecClaim("TI-1")]
[SpecClaim("EX-1")]
public static class Formatter
{
    /// <summary>
    /// Primary entry point — variadic-args overload mirroring
    /// <c>template&lt;typename... Args&gt; string format(string_view fmt, Args...)</c>.
    /// </summary>
    [SpecClaim("INV-1")]
    [SpecClaim("INV-2")]
    [SpecClaim("OL-1")]
    public static string Format(string fmtstr, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(fmtstr);
        ArgumentNullException.ThrowIfNull(args);
        var spec = FormatString.Parse(fmtstr);
        if (spec.PlaceholderCount != args.Length)
            throw new FormatException(
                $"format string expects {spec.PlaceholderCount} args, got {args.Length}");

        // TODO(impl): walk spec.Tokens, dispatch each Placeholder to the
        // matching arg via the visitor below, then concatenate via
        // StringBuilder. The C++ source uses a constexpr-evaluated tuple-
        // visit; the .NET equivalent is a hot-path-friendly switch on
        // typeof(args[i]).
        throw new NotImplementedException(
            "Format body — fill from signed spec INV-1, INV-2, OL-1.");
    }

    /// <summary>
    /// Strongly-typed integral overload — mirrors the <c>fmt::detail::format_int</c>
    /// fast-path. UB-1 is defensively guarded (INT_MIN special-case).
    /// </summary>
    [SpecClaim("INV-3")]
    [SpecClaim("UB-1")]
    public static string FormatInt(long value, FormatSpec? spec = null)
    {
        // TODO(impl): write ASCII digits into a stackalloc buffer; defensively
        // handle long.MinValue via unsigned absolute value (per UB-1).
        throw new NotImplementedException(
            "FormatInt body — fill from signed spec INV-3, UB-1.");
    }
}

/// <summary>
/// Per-placeholder format spec: width, precision, type-letter, fill char.
/// Mirrors fmt's <c>format_specs</c>. Records only — no behaviour beyond
/// the parser produces them.
/// </summary>
public sealed record FormatSpec(int Width, int Precision, char TypeLetter, char Fill);
