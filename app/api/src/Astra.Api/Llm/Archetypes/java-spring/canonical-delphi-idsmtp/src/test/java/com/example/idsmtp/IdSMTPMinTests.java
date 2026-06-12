// SPDX-Spec: delphi/TIdSMTPMin (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// JUnit 5 fixtures tied 1:1 to signed-spec claims. Each @Tag("claim:...") is the
// link back to the spec; the equivalence sidecar reads these tags to know which
// spec claims a passing test covers. Test bodies are TODO until the connection
// class is implemented; the assertion shapes are not.

package com.example.idsmtp;

import org.junit.jupiter.api.Disabled;
import org.junit.jupiter.api.Tag;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class IdSMTPMinTests {

    @Test
    @Tag("claim:INV-1")
    void port_outOfRange_throws() {
        try (var c = new IdSMTPMin()) {
            assertThrows(IllegalArgumentException.class, () -> c.setPort(0));
            assertThrows(IllegalArgumentException.class, () -> c.setPort(65_536));
        }
    }

    @Test
    @Tag("claim:INV-2")
    void close_beforeOpen_isIdempotent() {
        var c = new IdSMTPMin();
        assertDoesNotThrow(c::close);
        assertDoesNotThrow(c::close); // INV-2: idempotent on already-closed.
    }

    @Test
    @Tag("claim:OL-1")
    void closeReleasesResources_andSettersThrow() {
        var c = new IdSMTPMin();
        c.setHost("localhost");
        c.setPort(2525);
        c.close();
        // After close, every mutator must throw — OL-1.
        assertThrows(IllegalStateException.class, () -> c.setHost("elsewhere"));
    }

    @Test
    @Tag("claim:EC-1")
    void sendCmd_beforeOpen_throwsIllegalState() {
        try (var c = new IdSMTPMin()) {
            assertThrows(IllegalStateException.class,
                    () -> c.sendCmd("HELO localhost", 250));
        }
    }

    @Test
    @Tag("claim:EC-2")
    @Disabled("Requires a live SMTP fixture. Property-test sidecar (Phase 9.0.g) covers this.")
    void close_afterPartialCommand_stillCleansUp() {
        // EC-2: caller sends MAIL FROM, never sends RCPT TO, then calls close().
        // Expected: close() sends QUIT and fires status DISCONNECTED — even mid-conversation.
    }
}
