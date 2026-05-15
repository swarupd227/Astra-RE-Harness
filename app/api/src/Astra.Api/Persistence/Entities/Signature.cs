namespace Astra.Api.Persistence.Entities;

public sealed class Signature
{
    public Guid Id { get; set; }
    public Guid SpecId { get; set; }
    public Guid? SignerId { get; set; }
    public string SignerDisplay { get; set; } = "";   // captured at sign-time
    public DateTimeOffset SignedAt { get; set; }

    /// <summary>SHA-256 hash of the source-version's file manifest.</summary>
    public string SourceVersionHash { get; set; } = "";

    /// <summary>SHA-256 hash of the canonicalized spec_json.</summary>
    public string SpecCanonicalHash { get; set; } = "";

    /// <summary>RS256 signature of <see cref="SpecCanonicalHash"/>.</summary>
    public byte[] SignatureBytes { get; set; } = Array.Empty<byte>();

    /// <summary>Identifier of the signing key in this environment.</summary>
    public string SignatureKeyId { get; set; } = "";

    /// <summary>Algorithm used (e.g. "RS256").</summary>
    public string Algorithm { get; set; } = "RS256";

    /// <summary>Where the canonicalized signed JSON was archived.</summary>
    public string SignedBlobUri { get; set; } = "";

    public Spec? Spec { get; set; }
}
