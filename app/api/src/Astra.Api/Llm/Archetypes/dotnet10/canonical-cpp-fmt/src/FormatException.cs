// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// Exception type for format-string failures. Per spec claim EX-1 the routine
// gives the BASIC exception guarantee: on throw the partial output is not
// returned, no observable state outside the returned string is mutated.
// The corresponding C++ type is fmt::format_error (which derives from
// std::runtime_error); the .NET projection shadows the BCL System.FormatException
// namespace because the BCL type does NOT carry the offending-index field
// the spec requires.

namespace Demo.Fmt;

/// <summary>
/// Thrown when the format string is malformed or an argument's runtime type
/// is incompatible with its placeholder spec.
/// </summary>
[SpecClaim("EX-1")]
public sealed class FormatException : System.FormatException
{
    /// <summary>0-based character index in the original format string.</summary>
    public int OffendingIndex { get; }

    public FormatException(string message) : base(message)
    {
        OffendingIndex = -1;
    }

    public FormatException(string message, int offendingIndex) : base(message)
    {
        OffendingIndex = offendingIndex;
    }
}
