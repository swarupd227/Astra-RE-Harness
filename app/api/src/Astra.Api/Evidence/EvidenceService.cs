using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Api.Artefacts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Evidence;

public class EvidenceSummary
{
    public string ShortId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public string TrustTier { get; init; } = "";
    public string Label { get; init; } = "";
    public string Path { get; init; } = "";
    public string Locator { get; init; } = "";
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }
    public Guid FileId { get; init; }
}

/// <summary>A window of the file with the cited lines marked, for the evidence viewer.</summary>
public sealed record EvidenceView(int StartLine, int EndLine, int HighlightStart, int HighlightEnd, string Text, bool Truncated);

public sealed class EvidenceDetail : EvidenceSummary
{
    /// <summary>The cited text itself.</summary>
    public string? Text { get; init; }
    /// <summary>Lines around the cited ones; null when the locator is not a line range.</summary>
    public EvidenceView? View { get; init; }
    public JsonElement? Structured { get; init; }
    public long? SizeBytes { get; init; }
    /// <summary>The chunks before and after this one in the same file.</summary>
    public string? Previous { get; init; }
    public string? Next { get; init; }
}

public sealed record EvidenceKindCount(string Kind, string Label, int Count);

public sealed record EvidencePage(IReadOnlyList<EvidenceSummary> Data, int Total, IReadOnlyList<EvidenceKindCount> Kinds);

/// <summary>A file whose chunks are to be (re)built from the content just stored for it.</summary>
public sealed record ArtefactIndexItem(SourceFile File, string? Text, byte[]? Bytes);

public sealed record IndexOutcome(IReadOnlyDictionary<Guid, int> ChunksByFile, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds and reads the evidence behind citations. Source code is indexed from the routines the
/// parser found (no text is copied: a code chunk is a span of the file); artefacts are indexed by
/// the adapter for their kind when they are attached. Anything not yet indexed (a project that
/// predates evidence, or an index that could not be built at the time) is built the first time it
/// is asked for, so a missing index never shows as missing evidence.
/// </summary>
public sealed class EvidenceService
{
    private const int InsertBatch = 1000;
    private const int ContextLines = 6;
    private const int MaxViewLines = 400;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> BuildLocks = new();

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly ILogger<EvidenceService> _logger;

    public EvidenceService(AppDbContext db, IBlobClient blob, ILogger<EvidenceService> logger)
    {
        _db = db;
        _blob = blob;
        _logger = logger;
    }

    // ── building ────────────────────────────────────────────────────────

    /// <summary>
    /// Brings a version's code chunks in line with its routines: adds what is missing, drops what
    /// no longer exists, leaves the rest (and their ids) alone. Returns the code chunk count.
    /// </summary>
    public async Task<int> BuildCodeAsync(Guid versionId, CancellationToken ct)
    {
        var gate = BuildLocks.GetOrAdd(versionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var spans = await (
                from s in _db.Subroutines.AsNoTracking()
                join f in _db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
                where f.SourceVersionId == versionId && f.Kind == ArtefactKinds.Code
                select new { FileId = f.Id, f.RelativePath, f.FileHash, SubId = s.Id, s.Name, s.LineStart, s.LineEnd, s.SourceLanguage })
                .ToListAsync(ct);

            var existing = await (
                from c in _db.EvidenceChunks.AsNoTracking()
                join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
                where c.SourceVersionId == versionId && c.Kind == ArtefactKinds.Code
                select new { c.Id, c.SourceFileId, c.Locator, c.ShortId, Path = f.RelativePath })
                .ToListAsync(ct);

            // One chunk per (file, locator): two routines on the same span share it.
            var desired = new Dictionary<(Guid FileId, string Locator), (string Path, string FileHash, EvidenceChunkDraft Draft)>();
            foreach (var s in spans)
            {
                var draft = CodeEvidence.ToDraft(new RoutineSpan(s.SubId, s.RelativePath, s.FileHash, s.Name, s.LineStart, s.LineEnd, s.SourceLanguage));
                desired.TryAdd((s.FileId, draft.Locator), (s.RelativePath, s.FileHash, draft));
            }

            var stale = existing.Where(e => !desired.ContainsKey((e.SourceFileId, e.Locator))).Select(e => e.Id).ToList();
            if (stale.Count > 0)
            {
                foreach (var chunk in stale.Chunk(5000))
                    await _db.EvidenceChunks.Where(c => chunk.Contains(c.Id)).ExecuteDeleteAsync(ct);
            }

            var staleSet = stale.ToHashSet();
            var kept = existing.Where(e => !staleSet.Contains(e.Id)).ToList();
            var keptKeys = kept.Select(e => (e.SourceFileId, e.Locator)).ToHashSet();
            var fresh = desired.Where(kv => !keptKeys.Contains(kv.Key)).ToList();
            if (fresh.Count == 0) return kept.Count;

            var taken = kept.ToDictionary(e => e.ShortId, e => EvidenceIds.KeyOf(e.Path, e.Locator), StringComparer.Ordinal);
            var ids = EvidenceIds.Assign(
                fresh.Select(kv => (ArtefactKinds.Code, kv.Value.Path, kv.Value.Draft.Locator)).ToList(), taken);

            var now = DateTimeOffset.UtcNow;
            var rows = new List<EvidenceChunk>(fresh.Count);
            for (var i = 0; i < fresh.Count; i++)
            {
                var (key, v) = (fresh[i].Key, fresh[i].Value);
                rows.Add(new EvidenceChunk
                {
                    Id = Guid.NewGuid(),
                    ShortId = ids[i],
                    SourceVersionId = versionId,
                    SourceFileId = key.FileId,
                    Kind = ArtefactKinds.Code,
                    TrustTier = EvidenceAdapters.TrustTierOf(ArtefactKinds.Code),
                    Locator = v.Draft.Locator,
                    Label = Trim(v.Draft.Label, 300),
                    Text = null,
                    StructuredJson = v.Draft.StructuredJson,
                    TextHash = CodeEvidence.TextHash(v.FileHash, v.Draft.Locator),
                    LineStart = v.Draft.LineStart,
                    LineEnd = v.Draft.LineEnd,
                    CreatedAt = now,
                });
            }
            await InsertAsync(rows, ct);
            return kept.Count + rows.Count;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Replaces the chunks of each given file with ones built from the content just stored for it.
    /// A file whose adapter yields nothing (a recording, a Word file before its adapter exists) is
    /// left with no chunks.
    /// </summary>
    public async Task<IndexOutcome> IndexFilesAsync(IReadOnlyList<ArtefactIndexItem> items, CancellationToken ct)
    {
        var counts = new Dictionary<Guid, int>();
        var warnings = new List<string>();

        foreach (var item in items)
        {
            var file = item.File;
            await _db.EvidenceChunks.Where(c => c.SourceFileId == file.Id).ExecuteDeleteAsync(ct);

            var built = EvidenceAdapters.Build(new AdapterInput(file.RelativePath, file.Kind, item.Text, item.Bytes));
            warnings.AddRange(built.Warnings);
            if (built.Chunks.Count == 0)
            {
                counts[file.Id] = 0;
                continue;
            }

            // A hash collision with another file's chunk is vanishingly rare; if one exists, step round it.
            var candidates = built.Chunks
                .SelectMany(c =>
                {
                    var b = EvidenceIds.Base(file.Kind, file.RelativePath, c.Locator);
                    return new[] { b, b + "-2", b + "-3", b + "-4" };
                })
                .Distinct()
                .ToList();
            var taken = await TakenAsync(file.SourceVersionId, candidates, ct);
            var ids = EvidenceIds.Assign(
                built.Chunks.Select(c => (file.Kind, file.RelativePath, c.Locator)).ToList(), taken);

            var tier = EvidenceAdapters.TrustTierOf(file.Kind);
            var now = DateTimeOffset.UtcNow;
            var rows = new List<EvidenceChunk>(built.Chunks.Count);
            for (var i = 0; i < built.Chunks.Count; i++)
            {
                var d = built.Chunks[i];
                rows.Add(new EvidenceChunk
                {
                    Id = Guid.NewGuid(),
                    ShortId = ids[i],
                    SourceVersionId = file.SourceVersionId,
                    SourceFileId = file.Id,
                    Kind = file.Kind,
                    TrustTier = tier,
                    Locator = d.Locator,
                    Label = Trim(d.Label, 300),
                    Text = d.Text,
                    StructuredJson = d.StructuredJson,
                    TextHash = d.Text is null ? CodeEvidence.TextHash(file.FileHash, d.Locator) : EvidenceAdapters.TextHash(d.Text),
                    LineStart = d.LineStart,
                    LineEnd = d.LineEnd,
                    CreatedAt = now,
                });
            }
            await InsertAsync(rows, ct);
            counts[file.Id] = rows.Count;
        }
        return new IndexOutcome(counts, warnings);
    }

    /// <summary>
    /// Makes sure a version's evidence is built before it is read: its code (indexed at ingest, but
    /// not for projects that predate evidence) and any text artefact attached without an index.
    /// Never throws: a failure is logged and the read goes on with what exists.
    /// </summary>
    public async Task EnsureIndexedAsync(Guid versionId, CancellationToken ct)
    {
        try
        {
            if (!await _db.EvidenceChunks.AnyAsync(c => c.SourceVersionId == versionId && c.Kind == ArtefactKinds.Code, ct)
                && await _db.Subroutines.AnyAsync(s => s.SourceFile!.SourceVersionId == versionId && s.SourceFile.Kind == ArtefactKinds.Code, ct))
            {
                await BuildCodeAsync(versionId, ct);
            }

            var unindexed = await _db.SourceFiles
                .Where(f => f.SourceVersionId == versionId && f.Kind != ArtefactKinds.Code
                            && !_db.EvidenceChunks.Any(c => c.SourceFileId == f.Id))
                .ToListAsync(ct);
            var items = new List<ArtefactIndexItem>();
            foreach (var f in unindexed)
            {
                if (ArtefactContentTypes.IsBinary(System.IO.Path.GetExtension(f.RelativePath))) continue;
                items.Add(new ArtefactIndexItem(f, await _blob.GetTextAsync(f.BlobUri, ct), null));
            }
            if (items.Count > 0) await IndexFilesAsync(items, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Building the evidence index for version {Version} failed; reading what exists", versionId);
        }
    }

    // ── reading ─────────────────────────────────────────────────────────

    /// <summary>The version to read: the one named, if it belongs to the project, else the project's current one.</summary>
    public async Task<(bool CorpusFound, Guid? VersionId)> ResolveVersionAsync(Guid corpusId, Guid? versionId, CancellationToken ct)
    {
        var current = await _db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => new { c.LatestVersionId })
            .FirstOrDefaultAsync(ct);
        if (current is null) return (false, null);
        if (versionId is not { } v) return (true, current.LatestVersionId);
        var belongs = await _db.SourceVersions.AsNoTracking().AnyAsync(x => x.Id == v && x.CorpusId == corpusId, ct);
        return (true, belongs ? v : null);
    }

    public async Task<EvidencePage> ListAsync(
        Guid versionId, string? kind, string? query, string? path, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var baseQuery =
            from c in _db.EvidenceChunks.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
            where c.SourceVersionId == versionId
            select new { Chunk = c, f.RelativePath };

        if (!string.IsNullOrWhiteSpace(path))
        {
            var p = path.Trim();
            baseQuery = baseQuery.Where(x => x.RelativePath == p);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            baseQuery = baseQuery.Where(x =>
                EF.Functions.ILike(x.Chunk.Label, like, "\\")
                || EF.Functions.ILike(x.RelativePath, like, "\\")
                || EF.Functions.ILike(x.Chunk.ShortId, like, "\\")
                || (x.Chunk.Text != null && EF.Functions.ILike(x.Chunk.Text, like, "\\")));
        }

        var facets = await baseQuery.GroupBy(x => x.Chunk.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var filtered = string.IsNullOrWhiteSpace(kind) ? baseQuery : baseQuery.Where(x => x.Chunk.Kind == kind);
        var total = await filtered.CountAsync(ct);
        var rows = await filtered
            .OrderBy(x => x.RelativePath)
            .ThenBy(x => x.Chunk.LineStart ?? int.MaxValue)
            .ThenBy(x => x.Chunk.Locator)
            .Skip(offset).Take(limit)
            .Select(x => new
            {
                x.Chunk.ShortId, x.Chunk.Kind, x.Chunk.TrustTier, x.Chunk.Label, x.Chunk.Locator,
                x.Chunk.LineStart, x.Chunk.LineEnd, x.Chunk.SourceFileId, x.RelativePath,
            })
            .ToListAsync(ct);

        return new EvidencePage(
            rows.Select(r => Summarise(r.ShortId, r.Kind, r.TrustTier, r.Label, r.RelativePath, r.Locator, r.LineStart, r.LineEnd, r.SourceFileId)).ToList(),
            total,
            facets.OrderBy(f => ArtefactKinds.OrderOf(f.Kind))
                  .Select(f => new EvidenceKindCount(f.Kind, ArtefactKinds.Find(f.Kind)?.Label ?? f.Kind, f.Count))
                  .ToList());
    }

    /// <summary>Summaries for a set of ids, in the order asked; an id that is not in this version is simply absent.</summary>
    public async Task<IReadOnlyDictionary<string, EvidenceSummary>> ResolveAsync(
        Guid versionId, IReadOnlyCollection<string> shortIds, CancellationToken ct)
    {
        if (shortIds.Count == 0) return new Dictionary<string, EvidenceSummary>();
        var rows = await (
            from c in _db.EvidenceChunks.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
            where c.SourceVersionId == versionId && shortIds.Contains(c.ShortId)
            select new { c.ShortId, c.Kind, c.TrustTier, c.Label, c.Locator, c.LineStart, c.LineEnd, c.SourceFileId, f.RelativePath })
            .ToListAsync(ct);
        return rows.ToDictionary(
            r => r.ShortId,
            r => Summarise(r.ShortId, r.Kind, r.TrustTier, r.Label, r.RelativePath, r.Locator, r.LineStart, r.LineEnd, r.SourceFileId),
            StringComparer.Ordinal);
    }

    public async Task<EvidenceDetail?> GetDetailAsync(Guid versionId, string shortId, CancellationToken ct)
    {
        var row = await (
            from c in _db.EvidenceChunks.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
            where c.SourceVersionId == versionId && c.ShortId == shortId
            select new { Chunk = c, f.RelativePath, f.BlobUri, f.SizeBytes, f.Kind })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var c0 = row.Chunk;
        string? text = c0.Text;
        EvidenceView? view = null;

        if (EvidenceLocator.TryParseLines(c0.Locator, out var from, out var to) && !ArtefactContentTypes.IsBinary(System.IO.Path.GetExtension(row.RelativePath)))
        {
            view = await TryBuildViewAsync(row.BlobUri, from, to, ct);
            if (text is null && view is not null)
                text = SliceLines(view, from, to);
        }

        var siblings = await _db.EvidenceChunks.AsNoTracking()
            .Where(s => s.SourceFileId == c0.SourceFileId)
            .OrderBy(s => s.LineStart ?? int.MaxValue).ThenBy(s => s.Locator)
            .Select(s => s.ShortId)
            .ToListAsync(ct);
        var at = siblings.IndexOf(c0.ShortId);

        JsonElement? structured = null;
        if (!string.IsNullOrWhiteSpace(c0.StructuredJson))
        {
            try { structured = JsonDocument.Parse(c0.StructuredJson).RootElement.Clone(); }
            catch (JsonException) { /* a malformed blob is not worth failing the read for */ }
        }

        return new EvidenceDetail
        {
            ShortId = c0.ShortId,
            Kind = c0.Kind,
            KindLabel = ArtefactKinds.Find(c0.Kind)?.Label ?? c0.Kind,
            TrustTier = c0.TrustTier,
            Label = c0.Label,
            Path = row.RelativePath,
            Locator = c0.Locator,
            LineStart = c0.LineStart,
            LineEnd = c0.LineEnd,
            FileId = c0.SourceFileId,
            Text = text,
            View = view,
            Structured = structured,
            SizeBytes = row.SizeBytes,
            Previous = at > 0 ? siblings[at - 1] : null,
            Next = at >= 0 && at < siblings.Count - 1 ? siblings[at + 1] : null,
        };
    }

    /// <summary>Chunks per file for a version, for the Sources view.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountsByFileAsync(Guid versionId, CancellationToken ct) =>
        await _db.EvidenceChunks.AsNoTracking()
            .Where(c => c.SourceVersionId == versionId)
            .GroupBy(c => c.SourceFileId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<EvidenceView?> TryBuildViewAsync(string blobUri, int from, int to, CancellationToken ct)
    {
        string whole;
        try { whole = await _blob.GetTextAsync(blobUri, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read {Blob} to show evidence", blobUri);
            return null;
        }

        var lines = whole.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (count == 0 || from > count) return null;

        var hiTo = Math.Min(to, count);
        var truncated = hiTo - from + 1 > MaxViewLines;
        if (truncated) hiTo = from + MaxViewLines - 1;

        var start = Math.Max(1, from - ContextLines);
        var end = Math.Min(count, hiTo + ContextLines);
        var text = string.Join('\n', lines[(start - 1)..end]);
        return new EvidenceView(start, end, from, hiTo, text, truncated);
    }

    private static string SliceLines(EvidenceView view, int from, int to)
    {
        var lines = view.Text.Split('\n');
        var a = from - view.StartLine;
        var b = Math.Min(to, view.HighlightEnd) - view.StartLine;
        return string.Join('\n', lines[a..(b + 1)]);
    }

    private async Task<Dictionary<string, string>> TakenAsync(Guid versionId, List<string> candidates, CancellationToken ct)
    {
        var rows = await (
            from c in _db.EvidenceChunks.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
            where c.SourceVersionId == versionId && candidates.Contains(c.ShortId)
            select new { c.ShortId, f.RelativePath, c.Locator })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ShortId, r => EvidenceIds.KeyOf(r.RelativePath, r.Locator), StringComparer.Ordinal);
    }

    private async Task InsertAsync(List<EvidenceChunk> rows, CancellationToken ct)
    {
        foreach (var batch in rows.Chunk(InsertBatch))
        {
            _db.EvidenceChunks.AddRange(batch);
            await _db.SaveChangesAsync(ct);
            // Evidence is write-once here; do not keep thousands of rows tracked for the rest of the request.
            foreach (var row in batch) _db.Entry(row).State = EntityState.Detached;
        }
    }

    private EvidenceSummary Summarise(
        string shortId, string kind, string tier, string label, string path, string locator, int? lineStart, int? lineEnd, Guid fileId) => new()
    {
        ShortId = shortId,
        Kind = kind,
        KindLabel = ArtefactKinds.Find(kind)?.Label ?? kind,
        TrustTier = tier,
        Label = label,
        Path = path,
        Locator = locator,
        LineStart = lineStart,
        LineEnd = lineEnd,
        FileId = fileId,
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
