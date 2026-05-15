using System.Text;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Astra.Api.Storage;

public sealed class MinioBlobClient : IBlobClient
{
    private readonly IMinioClient _client;
    private readonly ILogger<MinioBlobClient> _logger;

    public MinioBlobClient(IMinioClient client, ILogger<MinioBlobClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            _ = await _client.ListBucketsAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MinIO ping failed");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct = default)
    {
        var result = await _client.ListBucketsAsync(ct);
        return result.Buckets.Select(b => b.Name).ToList();
    }

    public async Task<string> PutTextAsync(
        string bucket,
        string objectName,
        string content,
        string contentType = "text/plain",
        CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes);

        var args = new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectName)
            .WithStreamData(stream)
            .WithObjectSize(bytes.LongLength)
            .WithContentType(contentType);
        await _client.PutObjectAsync(args, ct);

        return $"minio://{bucket}/{objectName}";
    }

    public async Task<string> GetTextAsync(string blobUri, CancellationToken ct = default)
    {
        var (bucket, name) = ParseUri(blobUri);

        using var ms = new MemoryStream();
        var args = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(name)
            .WithCallbackStream(stream => stream.CopyTo(ms));
        await _client.GetObjectAsync(args, ct);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken ct = default)
    {
        try
        {
            var args = new StatObjectArgs().WithBucket(bucket).WithObject(objectName);
            _ = await _client.StatObjectAsync(args, ct);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
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
