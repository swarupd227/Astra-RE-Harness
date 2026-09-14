// SPDX-Spec: delphi/TIdSMTPMin (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// Derived from event_handler_contracts claim EH-1. In Delphi the connection class
// fires OnStatus (TIdStatusEvent) for connection-state transitions and OnDisconnect
// (TNotifyEvent) when the socket closes. The .NET projection uses the C# `event`
// keyword on a strongly-typed EventArgs, which is the idiomatic translation per the
// extraction prompt's rule #6 and ADR-025 row "TNotifyEvent".

namespace Demo.IdSMTP;

/// <summary>Status payload for <see cref="IdSMTPMin.Status"/>. EH-1.</summary>
public sealed record IdStatusEventArgs(IdConnectionStatus Status, string Detail);

/// <summary>Coarse state of the connection. Mirrors TIdSocketHandlerState in Indy.</summary>
public enum IdConnectionStatus
{
    Resolving,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected
}
