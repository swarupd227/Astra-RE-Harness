namespace Astra.Api.Storage;

public sealed class StorageOptions
{
    /// <summary>"minio" (default) or "azureblob" — selects the IBlobClient
    /// implementation registered in Program.cs. Local docker-compose never
    /// sets this, so it keeps using MinIO unchanged.</summary>
    public string Provider { get; set; } = "minio";

    public string Endpoint { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    /// <summary>Only used when Provider == "azureblob".</summary>
    public string AzureBlobConnectionString { get; set; } = "";

    public BucketOptions Buckets { get; set; } = new();

    public sealed class BucketOptions
    {
        public string Sources { get; set; } = "sources";
        public string SignedSpecs { get; set; } = "signed-specs";
        public string Scaffolds { get; set; } = "scaffolds";
        public string LlmDebug { get; set; } = "llm-debug-restricted";
    }
}
