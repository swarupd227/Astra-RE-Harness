namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Software-HSM signing key persisted in Postgres so signatures remain
/// verifiable across API restarts.  Production swaps this for Azure Key
/// Vault Managed HSM (Phase D) — same <see cref="Astra.Api.Signing.IHsmSigner"/> shape, different backend.
/// </summary>
public sealed class SigningKey
{
    public string Id { get; set; } = "";          // e.g. "astra-dev-1"
    public string Algorithm { get; set; } = "RS256";
    public string PublicKeyPem { get; set; } = "";
    public string PrivateKeyPem { get; set; } = ""; // dev-only — replaced by HSM in Phase D
    public DateTimeOffset CreatedAt { get; set; }
}
