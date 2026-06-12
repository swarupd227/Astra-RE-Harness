// SPDX-Spec: delphi/TIdSMTPMin (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// xUnit fixtures tied 1:1 to signed-spec claims. Each [Trait("claim", "...")] tag
// is the link back to the spec; the equivalence sidecar reads these traits to
// know which spec claims a passing test covers. Test bodies are TODO until the
// connection class is implemented; the assertion shapes are not.

using Demo.IdSMTP;
using Xunit;

namespace Demo.IdSMTP.Tests;

public class IdSMTPMinTests
{
    [Fact]
    [Trait("claim", "INV-1")]
    public void Port_OutOfRange_Throws()
    {
        using var c = new IdSMTPMin();
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Port = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Port = 65_536);
    }

    [Fact]
    [Trait("claim", "INV-2")]
    public void Close_BeforeOpen_IsIdempotent()
    {
        using var c = new IdSMTPMin();
        var ex = Record.Exception(() => c.Close());
        Assert.Null(ex); // INV-2.
    }

    [Fact]
    [Trait("claim", "OL-1")]
    public void Dispose_ReleasesSocket()
    {
        var c = new IdSMTPMin { Host = "localhost", Port = 2525 };
        c.Dispose();
        // After Dispose, every method must throw ObjectDisposedException — OL-1.
        Assert.Throws<ObjectDisposedException>(() => c.Host = "elsewhere");
    }

    [Fact]
    [Trait("claim", "EC-1")]
    public void SendCmd_BeforeOpen_ThrowsInvalidOperation()
    {
        using var c = new IdSMTPMin();
        Assert.Throws<InvalidOperationException>(() => c.SendCmd("HELO localhost", 250));
    }

    [Fact(Skip = "Requires a live SMTP fixture. Property-test sidecar (Phase 9.0.g) covers this.")]
    [Trait("claim", "EC-2")]
    public void Close_AfterPartialCommand_StillCleansUp()
    {
        // EC-2: caller sends MAIL FROM, never sends RCPT TO, then calls Close.
        // Expected: Close() sends QUIT and reports Disconnect — even mid-conversation.
    }
}
