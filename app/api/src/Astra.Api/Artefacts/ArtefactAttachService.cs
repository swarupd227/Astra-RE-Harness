using System.Security.Cryptography;
using System.Text;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Evidence;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Artefacts;

public sealed record IncomingArtefact(string Path, byte[] Content, string? Kind = null);

/// <summary>What happened to one file. Status is added | replaced | unchanged | rejected.</summary>
public sealed record AttachFileResult(
    string Path,
    string? Kind,
    string Status,
    string? Code,
    string? Message,
    long SizeBytes,
    int LineCount,
    bool Ambiguous,
    /// <summary>How many citable evidence chunks the file was indexed into (0 when none, or not indexed yet).</summary>
    int Chunks = 0);

public sealed record AttachOutcome(Guid CorpusId, Guid VersionId, IReadOnlyList<AttachFileResult> Files)
{
    public int Count(string status) => Files.Count(f => f.Status == status);
}

public sealed record AttachResult(AttachOutcome? Outcome, string? ErrorCode)
{
    public static AttachResult Failure(string code) => new(null, code);
}

/// <summary>
/// Adds, replaces and removes non-code artefacts on a project's CURRENT source version.
/// Source code is not handled here: it comes in through ingest and re-sync, which build
/// whole versions. Artefacts arrive on their own schedule (a transcript weeks later, a
/// fresh ticket export), so they attach to the live version by path, and a later re-sync
/// carries them forward.
/// </summary>
public sealed class ArtefactAttachService
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly EvidenceService _evidence;
    private readonly ILogger<ArtefactAttachService> _logger;

    public ArtefactAttachService(
        AppDbContext db, IBlobClient blob, StorageOptions storage, IAuditLogger audit, EvidenceService evidence,
        ILogger<ArtefactAttachService> logger)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _evidence = evidence;
        _logger = logger;
    }

    public async Task<AttachResult> AttachAsync(
        Guid corpusId, IReadOnlyList<IncomingArtefact> incoming, DevPersonaContext actor, HttpContext? http, CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => new { c.Id, c.LatestVersionId, c.SettingsJson })
            .FirstOrDefaultAsync(ct);
        if (corpus is null) return AttachResult.Failure("corpus.not_found");
        if (corpus.LatestVersionId is not { } versionId) return AttachResult.Failure("attach.no_version");

        var settings = ProjectSettings.Parse(corpus.SettingsJson);
        var existing = await _db.SourceFiles
            .Where(f => f.SourceVersionId == versionId)
            .ToDictionaryAsync(f => f.RelativePath, StringComparer.Ordinal, ct);

        var results = new List<AttachFileResult>(incoming.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var toIndex = new List<ArtefactIndexItem>();
        var changed = false;

        foreach (var file in incoming)
        {
            var placement = ArtefactPlacement.Resolve(file.Path, settings, file.Kind);
            if (!placement.Accepted)
            {
                results.Add(Rejected(placement.Path, null, placement.Code!, placement.Message!, file.Content.LongLength));
                continue;
            }

            var path = placement.Path;
            var kind = ArtefactKinds.Find(placement.Kind)!;
            if (!seen.Add(path))
            {
                results.Add(Rejected(path, kind.Id, "duplicate_path_in_request",
                    "The same path appears twice in this upload; only the first was used.", file.Content.LongLength));
                continue;
            }
            if (file.Content.Length == 0)
            {
                results.Add(Rejected(path, kind.Id, "empty_file", "The file is empty.", 0));
                continue;
            }
            if (file.Content.LongLength > kind.MaxBytes)
            {
                results.Add(Rejected(path, kind.Id, "file_too_large",
                    $"The file is {file.Content.LongLength / (1024 * 1024):N0} MiB; {kind.Label} files are limited to {kind.MaxBytes / (1024 * 1024)} MiB.",
                    file.Content.LongLength));
                continue;
            }

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var binary = ArtefactContentTypes.IsBinary(ext);

            string? text = null;
            string hash;
            int lines;
            long size;
            if (binary)
            {
                hash = "sha256:" + Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant();
                lines = 0;
                size = file.Content.LongLength;
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(file.Content);
                }
                catch (DecoderFallbackException)
                {
                    results.Add(Rejected(path, kind.Id, "not_utf8",
                        "The file is not valid UTF-8 text. Save it as UTF-8 and try again.", file.Content.LongLength));
                    continue;
                }
                text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace("\r", "\n");
                var bytes = Encoding.UTF8.GetBytes(text);
                hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                lines = string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
                size = bytes.LongLength;
            }

            if (existing.TryGetValue(path, out var row))
            {
                if (row.Kind == ArtefactKinds.Code)
                {
                    results.Add(Rejected(path, kind.Id, "path_taken_by_code",
                        "A source-code file already has this path; an artefact cannot replace it.", size));
                    continue;
                }
                if (row.FileHash == hash && row.Kind == kind.Id)
                {
                    results.Add(new AttachFileResult(path, kind.Id, "unchanged", null, null, row.SizeBytes ?? size, row.LineCount, placement.Ambiguous));
                    continue;
                }
                await StoreAsync(versionId, path, ext, binary, text, file.Content, ct);
                row.Kind = kind.Id;
                row.FileHash = hash;
                row.LineCount = lines;
                row.SizeBytes = size;
                row.ContentType = ArtefactContentTypes.For(ext);
                changed = true;
                toIndex.Add(new ArtefactIndexItem(row, text, binary ? file.Content : null));
                results.Add(new AttachFileResult(path, kind.Id, "replaced", null, null, size, lines, placement.Ambiguous));
                continue;
            }

            var uri = await StoreAsync(versionId, path, ext, binary, text, file.Content, ct);
            var created = new SourceFile
            {
                Id = Guid.NewGuid(),
                SourceVersionId = versionId,
                RelativePath = path,
                FileHash = hash,
                LineCount = lines,
                BlobUri = uri,
                Kind = kind.Id,
                ContentType = ArtefactContentTypes.For(ext),
                SizeBytes = size,
            };
            _db.SourceFiles.Add(created);
            existing[path] = created;
            changed = true;
            toIndex.Add(new ArtefactIndexItem(created, text, binary ? file.Content : null));
            results.Add(new AttachFileResult(path, kind.Id, "added", null, null, size, lines, placement.Ambiguous));
        }

        if (changed)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Attaching artefacts to corpus {Corpus} lost a race with another change", corpusId);
                return AttachResult.Failure("attach.conflict");
            }

            // Evidence is additive: if indexing fails the files are still attached, and the index is
            // built the first time the evidence is read.
            try
            {
                var indexed = await _evidence.IndexFilesAsync(toIndex, ct);
                var byPath = toIndex.ToDictionary(i => i.File.RelativePath, i => i.File.Id, StringComparer.Ordinal);
                for (var i = 0; i < results.Count; i++)
                {
                    if (byPath.TryGetValue(results[i].Path, out var fileId) && results[i].Status is "added" or "replaced"
                        && indexed.ChunksByFile.TryGetValue(fileId, out var n))
                        results[i] = results[i] with { Chunks = n };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Indexing evidence for corpus {Corpus} failed; it will be built when first read", corpusId);
            }
        }

        var outcome = new AttachOutcome(corpusId, versionId, results);
        if (changed)
        {
            await _audit.LogAsync(
                "corpus.artefacts.attached", "corpus", corpusId, actor,
                new
                {
                    versionId,
                    added = outcome.Count("added"),
                    replaced = outcome.Count("replaced"),
                    unchanged = outcome.Count("unchanged"),
                    rejected = outcome.Count("rejected"),
                    kinds = results.Where(r => r.Kind is not null && r.Status is "added" or "replaced").Select(r => r.Kind).Distinct().ToArray(),
                },
                http, ct);
        }
        return new AttachResult(outcome, null);
    }

    /// <summary>Removes one artefact (never a source-code file) from the current version. The blob stays.</summary>
    public async Task<string> RemoveAsync(
        Guid corpusId, string path, DevPersonaContext actor, HttpContext? http, CancellationToken ct)
    {
        var normalised = ArtefactPlacement.NormalisePath(path, out _);
        if (normalised is null) return "invalid_path";

        var versionId = await _db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => c.LatestVersionId)
            .FirstOrDefaultAsync(ct);
        if (versionId is null) return "not_found";

        var row = await _db.SourceFiles
            .FirstOrDefaultAsync(f => f.SourceVersionId == versionId && f.RelativePath == normalised, ct);
        if (row is null) return "not_found";
        if (row.Kind == ArtefactKinds.Code) return "is_code";

        _db.SourceFiles.Remove(row);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            "corpus.artefact.removed", "corpus", corpusId, actor,
            new { versionId, path = normalised, kind = row.Kind }, http, ct);
        return "removed";
    }

    /// <summary>Per-kind counts and sizes for the current version, plus the artefact files themselves.</summary>
    public async Task<object?> SummaryAsync(Guid corpusId, CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => new { c.Id, c.LatestVersionId, c.SettingsJson })
            .FirstOrDefaultAsync(ct);
        if (corpus is null) return null;

        var settings = ProjectSettings.Parse(corpus.SettingsJson);
        var files = new List<SummaryRow>();
        IReadOnlyDictionary<Guid, int> chunks = new Dictionary<Guid, int>();
        if (corpus.LatestVersionId is { } vid)
        {
            files = await _db.SourceFiles.AsNoTracking()
                .Where(f => f.SourceVersionId == vid)
                .Select(f => new SummaryRow(f.Id, f.RelativePath, f.Kind, f.SizeBytes, f.LineCount, f.FileHash))
                .ToListAsync(ct);
            chunks = await _evidence.CountsByFileAsync(vid, ct);
        }
        int ChunksOf(Guid fileId) => chunks.TryGetValue(fileId, out var n) ? n : 0;

        var kinds = ArtefactKinds.All.Select(k =>
        {
            var mine = files.Where(f => f.Kind == k.Id).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
            // Source files can number in the thousands and are listed elsewhere.
            var listed = new List<object>();
            if (!k.Core)
                listed.AddRange(mine.Select(f => (object)new { id = f.Id, path = f.RelativePath, sizeBytes = f.SizeBytes, lineCount = f.LineCount, hash = f.FileHash, chunkCount = ChunksOf(f.Id) }));
            return new
            {
                id = k.Id,
                label = k.Label,
                core = k.Core,
                enabled = k.Core || settings.Has(k.Id),
                trustTier = k.Trust.ToString().ToLowerInvariant(),
                extensions = k.Extensions,
                fileCount = mine.Count,
                totalBytes = mine.Sum(f => f.SizeBytes ?? 0),
                sizeKnown = mine.All(f => f.SizeBytes is not null),
                chunkCount = mine.Sum(f => ChunksOf(f.Id)),
                files = listed,
            };
        }).ToList();

        return new { corpusId = corpus.Id, versionId = corpus.LatestVersionId, kinds };
    }

    private async Task<string> StoreAsync(
        Guid versionId, string path, string ext, bool binary, string? text, byte[] raw, CancellationToken ct)
    {
        var key = $"{versionId}/{path}";
        var contentType = ArtefactContentTypes.For(ext);
        return binary
            ? await _blob.PutBytesAsync(_storage.Buckets.Sources, key, raw, contentType, ct)
            : await _blob.PutTextAsync(_storage.Buckets.Sources, key, text!, contentType, ct);
    }

    private static AttachFileResult Rejected(string path, string? kind, string code, string message, long size) =>
        new(path, kind, "rejected", code, message, size, 0, false);

    private sealed record SummaryRow(Guid Id, string RelativePath, string Kind, long? SizeBytes, int LineCount, string FileHash);
}
