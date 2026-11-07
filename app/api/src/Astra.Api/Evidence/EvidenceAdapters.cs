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

public sealed record AdapterInput(string Path, string Kind, string? Text, byte[]? Bytes)
{
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

public sealed record AdapterResult(IReadOnlyList<EvidenceChunkDraft> Chunks, IReadOnlyList<string> Warnings)
{
    public static readonly AdapterResult Empty = new(Array.Empty<EvidenceChunkDraft>(), Array.Empty<string>());

    public static AdapterResult Nothing(string warning) => new(Array.Empty<EvidenceChunkDraft>(), new[] { warning });
}

/// <summary>
/// Turns one artefact file into the chunks that can be cited. A kind's own adapter (tickets,
/// transcripts, configuration …) replaces the generic line-window one; adding a kind never touches
/// the parser sidecar. Adapters are pure: text or bytes in, drafts and warnings out, no I/O. A
/// warning is a plain sentence about this file (it is shown under the file, so it does not name it).
/// </summary>
public interface IEvidenceAdapter
{
    AdapterResult Build(AdapterInput input);
}

public static class EvidenceAdapters
{
    /// <summary>How many chunks one file may yield. A ticket export is the one kind that is legitimately large.</summary>
    public const int DefaultMaxChunks = 2_000;
    public const int TicketMaxChunks = 20_000;

    private static readonly ConfigAdapter Config = new();
    private static readonly TicketAdapter Ticket = new();
    private static readonly TranscriptAdapter Transcript = new();
    private static readonly MarkdownAdapter Markdown = new();
    private static readonly DocxAdapter Docx = new();
    private static readonly XmlElementAdapter Xml = new();

    /// <summary>
    /// The adapter for a file, chosen by its kind and extension. Null means the kind yields no chunks of
    /// its own (a recording is cited through the transcript it is linked to). Anything without a
    /// dedicated adapter is cut into line windows.
    /// </summary>
    public static IEvidenceAdapter? AdapterFor(string kind, string extension) => (kind, extension) switch
    {
        (ArtefactKinds.Audio, _) => null,
        (ArtefactKinds.Config, ".json") => Config,
        (ArtefactKinds.Ticket, ".csv") => Ticket,
        (ArtefactKinds.Transcript, ".vtt" or ".txt") => Transcript,
        (ArtefactKinds.Document, ".docx") => Docx,
        (ArtefactKinds.Document, ".md") => Markdown,
        (ArtefactKinds.Document or ArtefactKinds.Job or ArtefactKinds.Etl, ".xml" or ".dtsx") => Xml,
        _ => LineWindowAdapter.Instance,
    };

    /// <summary>True when the file has to be read as bytes to be indexed (a Word file is a zip).</summary>
    public static bool NeedsBytes(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when indexing this file can yield chunks at all.</summary>
    public static bool Indexes(string kind) => kind != ArtefactKinds.Audio;

    public static int MaxChunksFor(string kind) => kind == ArtefactKinds.Ticket ? TicketMaxChunks : DefaultMaxChunks;

    /// <summary>
    /// Chunks for an artefact file. Never throws: a file an adapter cannot read yields no chunks and a
    /// sentence saying why, so one bad file cannot stop the rest of an upload from being indexed.
    /// </summary>
    public static AdapterResult Build(AdapterInput input)
    {
        var adapter = AdapterFor(input.Kind, input.Extension);
        if (adapter is null || (input.Text is null && input.Bytes is null)) return AdapterResult.Empty;

        AdapterResult built;
        try
        {
            built = adapter.Build(input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return AdapterResult.Nothing($"It could not be read for indexing ({ex.GetType().Name}: {ex.Message}).");
        }
        return Finish(built, MaxChunksFor(input.Kind));
    }

    /// <summary>
    /// The checks every adapter's output gets, so no adapter has to remember them: no empty chunks, no
    /// chunk longer than the limit, locators unique within the file (the database enforces it), and a cap
    /// on how many chunks one file may produce.
    /// </summary>
    public static AdapterResult Finish(AdapterResult built, int maxChunks)
    {
        var warnings = new List<string>(built.Warnings);
        var kept = new List<EvidenceChunkDraft>(built.Chunks.Count);
        var clipped = 0;
        foreach (var c in built.Chunks)
        {
            if (c.Text is not null && string.IsNullOrWhiteSpace(c.Text)) continue;
            var d = c;
            if (d.Text is { Length: > LineWindowAdapter.MaxChunkChars } t)
            {
                d = d with { Text = t[..LineWindowAdapter.MaxChunkChars] };
                clipped++;
            }
            kept.Add(d);
        }
        if (clipped > 0 && !warnings.Any(w => w.Contains("cut to", StringComparison.Ordinal)))
            warnings.Add($"{clipped:N0} {(clipped == 1 ? "chunk was" : "chunks were")} longer than {LineWindowAdapter.MaxChunkChars:N0} characters and cut to that length.");

        if (kept.Count > maxChunks)
        {
            warnings.Add($"Only the first {maxChunks:N0} of {kept.Count:N0} pieces were indexed.");
            kept.RemoveRange(maxChunks, kept.Count - maxChunks);
        }
        return new AdapterResult(EnsureUniqueLocators(kept), warnings);
    }

    /// <summary>
    /// Two pieces of one file can end up with the same locator (a heading that appears twice, two turns
    /// that start in the same second). The later ones get <c>~2</c>, <c>~3</c> … so every piece stays
    /// citable on its own.
    /// </summary>
    public static IReadOnlyList<EvidenceChunkDraft> EnsureUniqueLocators(IReadOnlyList<EvidenceChunkDraft> drafts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EvidenceChunkDraft>(drafts.Count);
        foreach (var d in drafts)
        {
            var bounded = BoundLocator(d.Locator);
            var locator = bounded;
            for (var n = 2; !seen.Add(locator); n++) locator = $"{bounded}~{n}";
            result.Add(locator == d.Locator ? d : d with { Locator = locator });
        }
        return result;
    }

    /// <summary>The longest locator the database column holds.</summary>
    public const int MaxLocatorChars = 400;

    /// <summary>
    /// A locator longer than the column (a very deeply nested element) is cut and given a short hash of the
    /// whole, so it stays unique and stays the same for the same element.
    /// </summary>
    public static string BoundLocator(string locator)
    {
        if (locator.Length <= MaxLocatorChars - 10) return locator;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(locator)), 0, 4).ToLowerInvariant();
        return locator[..(MaxLocatorChars - 20)] + "…" + hash;
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
    public static readonly LineWindowAdapter Instance = new();

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
                warnings.Add($"Only the first {MaxChunksPerFile:N0} windows of this file were indexed.");
                break;
            }
            if (slice.Length > MaxChunkChars)
            {
                warnings.Add($"Lines {from + 1}–{to} are very long and were cut to {MaxChunkChars:N0} characters.");
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
