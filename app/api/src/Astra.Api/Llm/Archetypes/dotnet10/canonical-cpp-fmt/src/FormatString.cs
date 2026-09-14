// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// Compile-time format-string parser. The C++ source uses constexpr to validate
// the format string at compile time; the .NET projection validates at runtime
// (Roslyn analyzers could push the check earlier, but the spec doesn't require it).

namespace Demo.Fmt;

/// <summary>Token kinds emitted by <see cref="FormatString.Parse"/>.</summary>
public enum FormatTokenKind
{
    Literal,
    Placeholder,
    EscapedBrace,
}

public sealed record FormatToken(FormatTokenKind Kind, string Text, FormatSpec? Spec = null);

/// <summary>
/// Parsed format-string. <see cref="Tokens"/> is the literal-and-placeholder
/// sequence; <see cref="PlaceholderCount"/> is the number of arguments the
/// format string expects.
/// </summary>
public sealed record FormatString(IReadOnlyList<FormatToken> Tokens, int PlaceholderCount)
{
    /// <summary>
    /// Parse a format string into a token sequence.
    /// </summary>
    /// <remarks>
    /// INV-3: a balanced `{{` / `}}` pair emits ONE literal `{` or `}` token;
    /// EC-2 covers the unbalanced-brace case which throws
    /// <see cref="FormatException"/>.
    /// </remarks>
    [SpecClaim("INV-3")]
    [SpecClaim("EC-2")]
    public static FormatString Parse(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        // TODO(impl): walk `format` char-by-char, accumulating literal runs
        // and lifting `{...}` placeholders into FormatToken(Placeholder, ...,
        // spec). Handle `{{` and `}}` as escapes. Throw FormatException with
        // the offending index when a `{` has no matching `}`.
        throw new NotImplementedException(
            "FormatString.Parse body — fill from signed spec INV-3, EC-2.");
    }
}
