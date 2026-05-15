namespace Astra.Api.Signing;

/// <summary>
/// Same shape as the production Azure Key Vault Managed HSM signer that
/// lands in Phase D. Phase B.3 ships the software implementation —
/// keys live in Postgres and signing happens in-process. The interface
/// is identical so the swap is a registration change in Program.cs.
/// </summary>
public interface IHsmSigner
{
    /// <summary>Active signing key identifier (e.g. "astra-dev-1").</summary>
    string ActiveKeyId { get; }

    /// <summary>Signing algorithm (currently always "RS256").</summary>
    string Algorithm { get; }

    /// <summary>Returns the active public key in PEM form for verification.</summary>
    Task<string> GetPublicKeyPemAsync(CancellationToken ct = default);

    /// <summary>RS256-sign the SHA-256 hash of <paramref name="messageBytes"/>.</summary>
    Task<byte[]> SignAsync(byte[] messageBytes, CancellationToken ct = default);

    /// <summary>Verify an RS256 signature against the active key.</summary>
    Task<bool> VerifyAsync(byte[] messageBytes, byte[] signature, CancellationToken ct = default);
}
