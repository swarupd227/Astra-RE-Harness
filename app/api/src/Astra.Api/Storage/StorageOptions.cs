namespace Astra.Api.Storage;

public sealed class StorageOptions
{
    public string Endpoint { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public BucketOptions Buckets { get; set; } = new();

    public sealed class BucketOptions
    {
        public string Sources { get; set; } = "sources";
        public string SignedSpecs { get; set; } = "signed-specs";
        public string Scaffolds { get; set; } = "scaffolds";
        public string LlmDebug { get; set; } = "llm-debug-restricted";
    }
}
