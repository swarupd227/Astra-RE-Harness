// SPDX-Spec: delphi/TIdSMTPMin.Connect (signed)
// SPDX-Archetype: canonical-delphi-idsmtp
//
// Derived from interface_implementations claim II-1 (Delphi's IIdComponent contract).
// In Delphi, every component descended from TIdComponent implements IIdComponent —
// a marker-ish interface tying the host into the Indy connection lifecycle (Open /
// Close / status notifications). In .NET we model this as a small interface that
// the connection class implements; IDisposable is the canonical RAII contract for
// the Open/Close pair.

using System.Net.Sockets;

namespace Demo.IdSMTP;

/// <summary>
/// .NET projection of Delphi's <c>IIdComponent</c> contract — see spec claim II-1.
/// </summary>
public interface IIdComponent : IDisposable
{
    /// <summary>True once the underlying TCP socket has completed a handshake.</summary>
    bool Connected { get; }

    /// <summary>Open the socket. INV-1: must be called exactly once before any SendCmd.</summary>
    void Open();

    /// <summary>Close the socket. INV-2: idempotent — safe to call multiple times.</summary>
    void Close();
}
