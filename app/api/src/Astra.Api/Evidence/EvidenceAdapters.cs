using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Api.Artefacts;

namespace Astra.Api.Evidence;

/// <summary>One citable piece of a file, before it has an id or a database row.</summary>
public sealed record EvidenceChunkDraft(
    string Locator,
    string Label,
    /// <summary>The chunk's text. Null for source code, whose text is the file's own lines.</summary>
    string? Text,
    string? StructuredJson = null,
    int? LineStart = null,
    int? LineEnd = null);

public sealed record AdapterInput(string Path, string Kind, string? Text, byte[]? Bytes);

public sealed record AdapterResult(IReadOnlyList<EvidenceChunkDraft> Chunks, IReadOnlyList<string> Warnings)
{
    public static readonly AdapterResult Empty = new(Array.Empty<EvidenceChunkDraft>(), Array.Empty<string>());
}

/// <summary>
/// Turns one artefact file into the chunks that can be cited. A kind's own adapter (tickets,
/// transcripts, SQL …) replaces the generic line-window one; adding a kind never touches the
/// parser sidecar.
/// </summary>
public interface IEvidenceAdapter
{
    AdapterResult Build(AdapterInput input);
}

public static class EvidenceAdapters
{
    private static readonly LineWindowAdapter Generic = new();

    /// <summary>Chunks for an artefact file. Binary files have none until an adapter reads them.</summary>
    public static AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;
        return Generic.Build(input);
    }

    public static string TrustTierOf(string kind) =>
        (ArtefactKinds.Find(kind)?.Trust ?? TrustTier.Narrative).ToString().ToLowerInvariant();

    public static string TextHash(string text) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// Cuts a text file into windows of whole lines. It is the baseline for any text kind: good
/// enough to cite and to read, and exact about where the words are.
/// </summary>
public sealed class LineWindowAdapter : IEvidenceAdapter
{
    public const int WindowLines = 40;
    public const int MaxChunksPerFile = 2000;
    public const int MaxChunkChars = 20_000;

    public AdapterResult Build(AdapterInput input)
    {
        var text = input.Text ?? "";
        if (text.Length == 0) return AdapterResult.Empty;

        var lines = text.Split('\n');
        // A trailing newline leaves one empty element that is not a line of the file.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var name = input.Path[(input.Path.LastIndexOf('/') + 1)..];

        var chunks = new List<EvidenceChunkDraft>();
        var warnings = new List<string>();
        for (var from = 0; from < count; from += WindowLines)
        {
            var to = Math.Min(from + WindowLines, count);
            var slice = string.Join('\n', lines[from..to]);
            if (string.IsNullOrWhiteSpace(slice)) continue;

            if (chunks.Count >= MaxChunksPerFile)
            {
                warnings.Add($"{input.Path}: only the first {MaxChunksPerFile:N0} windows of this file were indexed.");
                break;
            }
            if (slice.Length > MaxChunkChars)
            {
                warnings.Add($"{input.Path}: lines {from + 1}–{to} are very long and were cut to {MaxChunkChars:N0} characters.");
                slice = slice[..MaxChunkChars];
            }
            chunks.Add(new EvidenceChunkDraft(
                EvidenceLocator.Lines(from + 1, to),
                $"{name} · lines {from + 1}–{to}",
                slice,
                LineStart: from + 1,
                LineEnd: to));
        }
        return new AdapterResult(chunks, warnings);
    }
}

/// <summary>A routine's span in a source file — the chunk a claim about that routine cites.</summary>
public sealed record RoutineSpan(
    Guid SubroutineId,
    string Path,
    string FileHash,
    string Name,
    int LineStart,
    int LineEnd,
    string Language);

public static class CodeEvidence
{
    public static EvidenceChunkDraft ToDraft(RoutineSpan r) => new(
        EvidenceLocator.Lines(r.LineStart, r.LineEnd),
        r.Name,
        Text: null,
        StructuredJson: JsonSerializer.Serialize(new { routine = r.Name, language = r.Language, subroutineId = r.SubroutineId }),
        LineStart: r.LineStart,
        LineEnd: r.LineEnd);

    /// <summary>
    /// Code chunks hold no text of their own, so their hash is of the file's hash and the span:
    /// it changes when the file does, without reading the file back at index time.
    /// </summary>
    public static string TextHash(string fileHash, string locator) =>
        "span:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileHash + "|" + locator))).ToLowerInvariant();
}
