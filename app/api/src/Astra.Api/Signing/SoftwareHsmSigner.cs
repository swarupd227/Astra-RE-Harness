using System.Security.Cryptography;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Signing;

/// <summary>
/// Phase B.3 software signer. RSA-SHA256 (RS256) with a 4096-bit key persisted
/// in Postgres. Generated once on first sign attempt; reused thereafter.
/// Phase D swaps this for an Azure Key Vault Managed HSM-backed implementation.
/// </summary>
public sealed class SoftwareHsmSigner : IHsmSigner
{
    public const string DefaultKeyId = "astra-dev-1";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SoftwareHsmSigner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RSA? _cachedRsa;
    private string? _cachedKeyId;

    public SoftwareHsmSigner(IServiceScopeFactory scopeFactory, ILogger<SoftwareHsmSigner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string ActiveKeyId => _cachedKeyId ?? DefaultKeyId;
    public string Algorithm => "RS256";

    public async Task<string> GetPublicKeyPemAsync(CancellationToken ct = default)
    {
        var rsa = await EnsureKeyAsync(ct);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    public async Task<byte[]> SignAsync(byte[] messageBytes, CancellationToken ct = default)
    {
        var rsa = await EnsureKeyAsync(ct);
        return rsa.SignData(messageBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public async Task<bool> VerifyAsync(byte[] messageBytes, byte[] signature, CancellationToken ct = default)
    {
        var rsa = await EnsureKeyAsync(ct);
        return rsa.VerifyData(messageBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private async Task<RSA> EnsureKeyAsync(CancellationToken ct)
    {
        if (_cachedRsa is not null) return _cachedRsa;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedRsa is not null) return _cachedRsa;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existing = await db.SigningKeys
                .OrderByDescending(k => k.CreatedAt)
                .FirstOrDefaultAsync(ct);

            RSA rsa;
            if (existing is null)
            {
                rsa = RSA.Create(4096);
                var entity = new SigningKey
                {
                    Id = DefaultKeyId,
                    Algorithm = "RS256",
                    PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(),
                    PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(),
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await db.SigningKeys.AddAsync(entity, ct);
                await db.SaveChangesAsync(ct);
                _cachedKeyId = entity.Id;
                _logger.LogInformation("Generated new software HSM signing key {KeyId}", entity.Id);
            }
            else
            {
                rsa = RSA.Create();
                rsa.ImportFromPem(existing.PrivateKeyPem);
                _cachedKeyId = existing.Id;
                _logger.LogInformation("Loaded software HSM signing key {KeyId}", existing.Id);
            }

            _cachedRsa = rsa;
            return rsa;
        }
        finally
        {
            _gate.Release();
        }
    }
}
