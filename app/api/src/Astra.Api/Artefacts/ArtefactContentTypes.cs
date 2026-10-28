namespace Astra.Api.Artefacts;

/// <summary>
/// Per-extension facts about artefact files. Whether a file is text or binary is a
/// property of its extension, not of its kind: a "documents" project takes both
/// <c>.md</c> (text) and <c>.docx</c> (binary).
/// </summary>
public static class ArtefactContentTypes
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".sql"] = "application/sql",
        [".ddl"] = "application/sql",
        [".xml"] = "application/xml",
        [".dtsx"] = "application/xml",
        [".json"] = "application/json",
        [".vtt"] = "text/vtt",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".md"] = "text/markdown",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".mp3", ".wav", ".m4a",
    };

    public static string For(string extension) =>
        Types.TryGetValue(extension, out var t) ? t : "application/octet-stream";

    /// <summary>True when the file must be stored and hashed as raw bytes, never decoded as text.</summary>
    public static bool IsBinary(string extension) => BinaryExtensions.Contains(extension);
}
