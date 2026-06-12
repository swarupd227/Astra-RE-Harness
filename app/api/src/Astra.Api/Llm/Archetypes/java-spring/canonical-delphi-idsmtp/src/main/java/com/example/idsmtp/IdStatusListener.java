// SPDX-Spec: delphi/TIdSMTPMin (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// Derived from event_handler_contracts claim EH-1. Java has no equivalent of C#'s
// `event` keyword — the canonical translation per the extraction prompt's rule #6
// is a Listener interface with addListener / removeListener on the firing class.
// The Delphi TIdStatusEvent payload (status code + detail string) is preserved
// as a value record so the contract is identical to the C# sibling archetype.

package com.example.idsmtp;

/** Coarse state of the connection. Mirrors TIdSocketHandlerState in Indy. */
enum IdConnectionStatus {
    RESOLVING,
    CONNECTING,
    CONNECTED,
    DISCONNECTING,
    DISCONNECTED
}

/** Status payload for {@link IdStatusListener}. EH-1. */
record IdStatusEvent(IdConnectionStatus status, String detail) {}

/**
 * Listener for {@link IdSMTPMin} connection-state transitions. Subscribers
 * receive a status event for every state change emitted on the wire.
 */
@FunctionalInterface
public interface IdStatusListener {
    void onStatus(IdStatusEvent event);
}
