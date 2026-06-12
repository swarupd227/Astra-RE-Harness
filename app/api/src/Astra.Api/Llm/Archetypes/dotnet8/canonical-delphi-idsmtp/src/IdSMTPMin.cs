// SPDX-Spec: delphi/TIdSMTPMin (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// .NET 8 projection of Delphi's TIdSMTPMin — Indy's minimal SMTP client. Each
// public surface carries a [SpecClaim(...)] attribute citing the signed claim id;
// reviewers can map the C# back to the spec without leaving the IDE. The
// implementation BODY is intentionally TODO — the scaffold is a contract, not a
// translation. The signed spec is the source of truth; the implementer fills the
// body and the equivalence sidecar (Phase 9.0.f / fpc-sidecar) plus the property-
// test sidecar (9.0.g) confirm behavioural parity.

using System.Net.Sockets;

namespace Demo.IdSMTP;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>
/// .NET 8 projection of Delphi's <c>TIdSMTPMin</c>. Owns a TCP connection to an
/// SMTP server and exposes the minimum command set needed for Phase 9.0's demo
/// (Connect → HELO → MAIL FROM → RCPT TO → DATA → QUIT).
/// </summary>
/// <remarks>
/// Object-lifetime model (OL-1): this class is <b>Owned</b> by its caller. The
/// caller must Dispose() it; the underlying TcpClient is closed in Dispose().
/// </remarks>
[SpecClaim("II-1")]
[SpecClaim("OL-1")]
public sealed class IdSMTPMin : IIdComponent
{
    private readonly TcpClient _socket = new();
    private string _host = string.Empty;
    private int _port = 25;
    private bool _disposed;

    /// <summary>Remote host. PA-1: property accessor, no side effects on read.</summary>
    [SpecClaim("PA-1")]
    public string Host
    {
        get => _host;
        set { ThrowIfDisposed(); _host = value ?? string.Empty; }
    }

    /// <summary>Remote port. INV-1: 1 ≤ Port ≤ 65535 enforced on set.</summary>
    [SpecClaim("INV-1")]
    [SpecClaim("PA-2")]
    public int Port
    {
        get => _port;
        set
        {
            ThrowIfDisposed();
            if (value is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(value), "Port must be in [1, 65535].");
            _port = value;
        }
    }

    /// <inheritdoc/>
    public bool Connected => !_disposed && _socket.Connected;

    /// <summary>Fired on connection-state transitions. EH-1.</summary>
    [SpecClaim("EH-1")]
    public event EventHandler<IdStatusEventArgs>? Status;

    /// <summary>Fired once the socket is fully closed. EH-2.</summary>
    [SpecClaim("EH-2")]
    public event EventHandler? Disconnect;

    /// <inheritdoc/>
    [SpecClaim("INV-1")]
    [SpecClaim("SE-1")]
    public void Open()
    {
        ThrowIfDisposed();
        if (Connected) return; // INV-1: idempotent on already-open.
        // TODO(impl): _socket.Connect(_host, _port);
        // TODO(impl): RaiseStatus(IdConnectionStatus.Connected, $"{_host}:{_port}");
        // TODO(impl): Read greeting line (220 ...) — emit EC-1 if not 220.
        throw new NotImplementedException("Connect body — fill from signed spec INV-1, SE-1.");
    }

    /// <inheritdoc/>
    [SpecClaim("INV-2")]
    public void Close()
    {
        if (_disposed || !_socket.Connected) return; // INV-2: idempotent.
        // TODO(impl): send "QUIT\r\n" if a command is in flight (EC-2).
        // TODO(impl): _socket.Close();
        // TODO(impl): RaiseStatus(IdConnectionStatus.Disconnected, "QUIT");
        Disconnect?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Send a raw SMTP command. SE-1: writes to the socket.</summary>
    [SpecClaim("SE-1")]
    [SpecClaim("EC-1")]
    public string SendCmd(string command, int expectedCode)
    {
        ThrowIfDisposed();
        if (!Connected) throw new InvalidOperationException("Not connected. Call Open() first (INV-1).");
        // TODO(impl): write `command + "\r\n"`; read response line; parse 3-digit code.
        // TODO(impl): if response.code != expectedCode → throw IdSmtpReplyException (EC-1).
        throw new NotImplementedException("SendCmd body — fill from signed spec SE-1, EC-1.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Close(); } finally { _socket.Dispose(); _disposed = true; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IdSMTPMin));
    }
}
