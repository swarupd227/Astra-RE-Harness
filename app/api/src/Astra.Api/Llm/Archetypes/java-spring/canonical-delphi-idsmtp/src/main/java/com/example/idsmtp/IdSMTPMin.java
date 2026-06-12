// SPDX-Spec: delphi/TIdSMTPMin (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// Java 17 + Spring Boot projection of Delphi's TIdSMTPMin. Every public surface
// carries a @SpecClaim annotation citing the signed claim id; reviewers can map
// the Java back to the spec without leaving the IDE. The implementation BODY is
// intentionally TODO — the scaffold is a contract, not a translation. The signed
// spec is the source of truth; the implementer fills the body and the equivalence
// sidecar (Phase 9.0.f / fpc-sidecar) plus the property-test sidecar (9.0.g)
// confirm behavioural parity.

package com.example.idsmtp;

import java.io.IOException;
import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;
import org.springframework.stereotype.Component;

@Retention(RetentionPolicy.RUNTIME)
@Repeatable(SpecClaim.Container.class)
@interface SpecClaim {
    String value();
    @Retention(RetentionPolicy.RUNTIME)
    @interface Container { SpecClaim[] value(); }
}

/**
 * Java 17 projection of Delphi's <code>TIdSMTPMin</code>. Owns a TCP connection
 * to an SMTP server and exposes the minimum command set needed for Phase 9.0's
 * demo (Connect → HELO → MAIL FROM → RCPT TO → DATA → QUIT).
 *
 * <p>Object-lifetime model (OL-1): this class is <b>Owned</b> by its caller.
 * The caller must call {@link #close()} (or use try-with-resources); the
 * underlying socket is closed in close().
 */
@Component
@SpecClaim("II-1")
@SpecClaim("OL-1")
public class IdSMTPMin implements IdComponent {

    private Socket socket;
    private String host = "";
    private int port = 25;
    private boolean closed;
    private final List<IdStatusListener> listeners = new ArrayList<>();

    // ---- getter/setter pair (Delphi property → Java accessors) -----------------

    /** Remote host. PA-1: property accessor, no side effects on read. */
    @SpecClaim("PA-1")
    public String getHost() { return host; }

    @SpecClaim("PA-1")
    public void setHost(String host) {
        throwIfClosed();
        this.host = Objects.requireNonNullElse(host, "");
    }

    /** Remote port. INV-1: 1 ≤ port ≤ 65535 enforced on set. */
    @SpecClaim("INV-1")
    @SpecClaim("PA-2")
    public int getPort() { return port; }

    @SpecClaim("INV-1")
    @SpecClaim("PA-2")
    public void setPort(int port) {
        throwIfClosed();
        if (port < 1 || port > 65535) {
            throw new IllegalArgumentException("Port must be in [1, 65535].");
        }
        this.port = port;
    }

    // ---- listener registration (Delphi event → Java listener) -------------------

    /** Subscribe to connection-state transitions. EH-1. */
    @SpecClaim("EH-1")
    public void addStatusListener(IdStatusListener l) { listeners.add(l); }

    public void removeStatusListener(IdStatusListener l) { listeners.remove(l); }

    private void fireStatus(IdConnectionStatus s, String detail) {
        var ev = new IdStatusEvent(s, detail);
        for (var l : listeners) l.onStatus(ev);
    }

    // ---- IdComponent contract ---------------------------------------------------

    @Override
    public boolean isConnected() {
        return !closed && socket != null && socket.isConnected();
    }

    @Override
    @SpecClaim("INV-1")
    @SpecClaim("SE-1")
    public void open() {
        throwIfClosed();
        if (isConnected()) return; // INV-1: idempotent on already-open.
        // TODO(impl): socket = new Socket(host, port);
        // TODO(impl): fireStatus(IdConnectionStatus.CONNECTED, host + ":" + port);
        // TODO(impl): read greeting line (220 ...) — emit EC-1 if not 220.
        throw new UnsupportedOperationException("Connect body — fill from signed spec INV-1, SE-1.");
    }

    @Override
    @SpecClaim("INV-2")
    public void close() {
        if (closed || socket == null) return; // INV-2: idempotent.
        try {
            // TODO(impl): send "QUIT\r\n" if a command is in flight (EC-2).
            socket.close();
            fireStatus(IdConnectionStatus.DISCONNECTED, "QUIT");
        } catch (IOException e) {
            throw new RuntimeException("Failed to close socket", e);
        } finally {
            closed = true;
        }
    }

    /** Send a raw SMTP command. SE-1: writes to the socket. */
    @SpecClaim("SE-1")
    @SpecClaim("EC-1")
    public String sendCmd(String command, int expectedCode) {
        throwIfClosed();
        if (!isConnected()) {
            throw new IllegalStateException("Not connected. Call open() first (INV-1).");
        }
        // TODO(impl): write command + "\r\n"; read response line; parse 3-digit code.
        // TODO(impl): if response.code != expectedCode → throw IdSmtpReplyException (EC-1).
        throw new UnsupportedOperationException("sendCmd body — fill from signed spec SE-1, EC-1.");
    }

    private void throwIfClosed() {
        if (closed) throw new IllegalStateException("IdSMTPMin already closed.");
    }
}
