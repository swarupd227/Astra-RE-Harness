namespace Astra.Api.Ingest;

/// <summary>
/// Decides whether a source version is parsed as one corpus (the sidecar
/// puts the files on disk so cross-file references resolve) or file by
/// file. Only C++ needs the corpus pass today: a .cpp whose class is
/// declared in a sibling header yields nothing when parsed alone. The
/// corpus request is a single gRPC message, so it is capped well under
/// the 32 MiB channel limit; bigger corpora fall back to per-file parsing
/// and say so.
/// </summary>
public static class IngestParseRouting
{
    public const long CorpusParseMaxBytes = 24L * 1024 * 1024;

    public static bool UseCorpusMode(IEnumerable<string> relativePaths, long totalBytes)
        => totalBytes <= CorpusParseMaxBytes
           && relativePaths.Any(p => SourceLanguageDetector.FromFilename(p) == SourceLanguageDetector.Cpp);
}
