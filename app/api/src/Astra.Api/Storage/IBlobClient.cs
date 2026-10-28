namespace Astra.Api.Storage;

public interface IBlobClient
{
    Task<bool> PingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct = default);

    /// <summary>
    /// Uploads UTF-8 text content. Returns the canonical blob URI:
    /// <c>minio://&lt;bucket&gt;/&lt;objectName&gt;</c>.
    /// </summary>
    Task<string> PutTextAsync(string bucket, string objectName, string content, string contentType = "text/plain", CancellationToken ct = default);

    /// <summary>
    /// Reads UTF-8 text content from a blob URI returned by <see cref="PutTextAsync"/>.
    /// </summary>
    Task<string> GetTextAsync(string blobUri, CancellationToken ct = default);

    /// <summary>
    /// Uploads raw bytes (audio, Word documents). Same URI scheme and overwrite
    /// behaviour as <see cref="PutTextAsync"/>.
    /// </summary>
    Task<string> PutBytesAsync(string bucket, string objectName, byte[] content, string contentType = "application/octet-stream", CancellationToken ct = default);

    /// <summary>Reads the raw bytes of a blob written by either put method.</summary>
    Task<byte[]> GetBytesAsync(string blobUri, CancellationToken ct = default);

    /// <summary>True if the object already exists at the given bucket/name.</summary>
    Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken ct = default);
}
