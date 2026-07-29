using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Astra.Api.Storage;

/// <summary>
/// Phase 16.1 — native Azure Blob Storage backend for IBlobClient, added
/// after MinIO-on-Azure-Files proved unreliable (MinIO's storage engine
/// needs POSIX file semantics — locking, atomic rename — that SMB-backed
/// Azure Files doesn't reliably provide across container restarts).
///
/// Reuses MinioBlobClient's "minio://&lt;bucket&gt;/&lt;objectName&gt;" URI
/// scheme rather than inventing a new one: several call sites (IngestPipeline,
/// TestPackGenerator, ConsumeRollSeed) construct that prefix directly as a
/// string rather than going through PutTextAsync's return value, so both
/// IBlobClient implementations have to agree on the same format for a blob
/// URI written by one provider to ever be readable — not that it matters in
/// practice, since a deployment picks exactly one provider, but it keeps the
/// scheme a provider-agnostic "this project's blob storage" marker rather
/// than a literal (and after this class exists, inaccurate) MinIO reference.
/// </summary>
public sealed class AzureBlobStorageClient : IBlobClient
{
    private readonly BlobServiceClient _client;
    private readonly ILogger<AzureBlobStorageClient> _logger;

    public AzureBlobStorageClient(BlobServiceClient client, ILogger<AzureBlobStorageClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            _ = await _client.GetPropertiesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Blob Storage ping failed");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct = default)
    {
        var names = new List<string>();
        await foreach (var container in _client.GetBlobContainersAsync(cancellationToken: ct))
        {
            names.Add(container.Name);
        }
        return names;
    }

    public async Task<string> PutTextAsync(
        string bucket,
        string objectName,
        string content,
        string contentType = "text/plain",
        CancellationToken ct = default)
    {
        var container = _client.GetBlobContainerClient(bucket);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var blob = container.GetBlobClient(objectName);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            ct);

        return $"minio://{bucket}/{objectName}";
    }

    public async Task<string> GetTextAsync(string blobUri, CancellationToken ct = default)
    {
        var (bucket, name) = ParseUri(blobUri);
        var blob = _client.GetBlobContainerClient(bucket).GetBlobClient(name);
        var response = await blob.DownloadContentAsync(ct);
        return response.Value.Content.ToString();
    }

    public async Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken ct = default)
    {
        var blob = _client.GetBlobContainerClient(bucket).GetBlobClient(objectName);
        var response = await blob.ExistsAsync(ct);
        return response.Value;
    }

    private static (string Bucket, string Name) ParseUri(string blobUri)
    {
        const string prefix = "minio://";
        if (!blobUri.StartsWith(prefix))
            throw new ArgumentException($"Expected minio:// URI, got '{blobUri}'.", nameof(blobUri));
        var rest = blobUri[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) throw new ArgumentException($"Malformed blob URI '{blobUri}'.", nameof(blobUri));
        return (rest[..slash], rest[(slash + 1)..]);
    }
}
