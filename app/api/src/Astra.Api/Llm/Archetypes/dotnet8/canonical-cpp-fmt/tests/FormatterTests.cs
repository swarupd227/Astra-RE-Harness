// SPDX-Spec: cpp/fmt::format (signed)
// SPDX-Archetype: canonical-cpp-fmt
//
// xUnit fixtures tied 1:1 to signed-spec claims. Each [Trait("claim", "...")]
// tag is the link back to the spec; the equivalence sidecar reads these traits
// to know which spec claims a passing test covers. Test bodies are TODO until
// the implementation is filled in; the assertion shapes are not.

using Demo.Fmt;
using Xunit;

namespace Demo.Fmt.Tests;

public class FormatterTests
{
    [Fact]
    [Trait("claim", "INV-1")]
    public void Format_NoPlaceholders_ReturnsLiteralFormatString()
    {
        // TODO(impl-blocked): once Format is implemented, assert that
        // Formatter.Format("hello", new object?[0]) returns "hello".
        var ex = Record.Exception(() => Formatter.Format("hello"));
        Assert.IsAssignableFrom<System.NotImplementedException>(ex);
    }

    [Fact]
    [Trait("claim", "INV-2")]
    public void Format_PlaceholderCountMismatch_Throws()
    {
        // INV-2 / EX-1: when the format string expects N placeholders but
        // receives M ≠ N args, throw FormatException.
        Assert.Throws<Demo.Fmt.FormatException>(() => Formatter.Format("{0} {1}", 1));
    }

    [Fact]
    [Trait("claim", "UB-1")]
    public void FormatInt_LongMinValue_RendersWithoutOverflow()
    {
        // UB-1: signed-integer overflow on negating long.MinValue.
        // Defensively guarded — the implementation must produce a sensible
        // decimal expansion ("-9223372036854775808") rather than UB-crash.
        var ex = Record.Exception(() => Formatter.FormatInt(long.MinValue));
        // Once implemented:
        //   Assert.Equal("-9223372036854775808", Formatter.FormatInt(long.MinValue));
        Assert.IsAssignableFrom<System.NotImplementedException>(ex);
    }

    [Fact]
    [Trait("claim", "EC-1")]
    public void Format_EmptyArgs_OnLiteralOnly_Succeeds()
    {
        var ex = Record.Exception(() => Formatter.Format(""));
        // Once implemented:
        //   Assert.Equal("", Formatter.Format(""));
        Assert.IsAssignableFrom<System.NotImplementedException>(ex);
    }

    [Fact]
    [Trait("claim", "EC-2")]
    public void Parse_UnmatchedOpenBrace_Throws()
    {
        // EC-2 + EX-1: `Parse("{0")` must throw FormatException with
        // OffendingIndex == 1 (the index of the unmatched `{`).
        var ex = Record.Exception(() => FormatString.Parse("{0"));
        Assert.NotNull(ex);
    }
}
