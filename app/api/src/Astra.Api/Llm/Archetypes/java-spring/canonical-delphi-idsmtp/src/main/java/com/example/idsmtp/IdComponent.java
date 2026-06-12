// SPDX-Spec: delphi/TIdSMTPMin.Connect (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// Derived from interface_implementations claim II-1 (Delphi's IIdComponent contract).
// In Delphi, every component descended from TIdComponent implements IIdComponent —
// a marker-ish interface tying the host into the Indy connection lifecycle. The
// Java projection extends AutoCloseable so the connection class participates in
// try-with-resources — the canonical Java RAII pattern per the extraction prompt's
// rule #4 and ADR-025 row "IInterface".

package com.example.idsmtp;

/** Java projection of Delphi's <code>IIdComponent</code> contract — see spec claim II-1. */
public interface IdComponent extends AutoCloseable {

    /** True once the underlying TCP socket has completed a handshake. */
    boolean isConnected();

    /** Open the socket. INV-1: must be called exactly once before any sendCmd. */
    void open();

    /** Close the socket. INV-2: idempotent — safe to call multiple times. */
    @Override
    void close();
}
