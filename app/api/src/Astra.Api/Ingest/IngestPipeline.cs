using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Parser;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Ingest;

/// <summary>
/// End-to-end ingest + parse pipeline. Given a name and a set of in-memory
/// Fortran source files, creates a <see cref="Corpus"/>, uploads each file
/// to MinIO (sha256-keyed), calls the parser sidecar per file, and persists
/// the resulting subroutines. State machine:
///
///     INGESTING → PARSING → PARSED         (happy path)
///     INGESTING → FAILED                   (storage/db error)
///     PARSING   → FAILED                   (parser RPC error)
///
/// Synchronous on purpose — for the corpus sizes Phase C handles (≤200
/// files, ≤200k LOC) the whole thing finishes in under a second. Hangfire
/// async lands later if real target corpora prove larger.
/// </summary>
public sealed class IngestPipeline
{
    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IFortranParserClient _parser;
    private readonly IAuditLogger _audit;
    private readonly DevPersonaContext _persona;
    private readonly ILogger<IngestPipeline> _logger;

    public IngestPipeline(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IFortranParserClient parser,
        IAuditLogger audit,
        DevPersonaContext persona,
        ILogger<IngestPipeline> logger)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _parser = parser;
        _audit = audit;
        _persona = persona;
        _logger = logger;
    }

    public sealed record IncomingFile(string RelativePath, string Content);

    public sealed record IngestRequest(
        string Name,
        string SourceType,            // "upload" | "git"
        string? SourceUrl,
        string? Branch,
        string? SourceRoot,
        IReadOnlyList<IncomingFile> Files);

    public sealed record IngestResult(
        Guid CorpusId,
        string State,
        int FileCount,
        int TotalLoc,
        int SubroutineCount,
        IReadOnlyList<string> Warnings,
        string? ErrorMessage);

    public sealed record ReingestRequest(
        Guid CorpusId,
        string? SourceUrl,
        string? Branch,
        string? SourceRoot,
        string? GitCommitHash,
        IReadOnlyList<IncomingFile> Files);

    public sealed record ReingestResult(
        Guid CorpusId,
        string State,
        int FileCount,
        int TotalLoc,
        int SubroutineCount,
        int CarriedForwardCount,
        int SupersededCount,
        IReadOnlyList<string> Warnings,
        string? ErrorMessage);

    public async Task<IngestResult> IngestAsync(IngestRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("Corpus name is required.", nameof(req));
        if (req.Files.Count == 0)
            throw new ArgumentException("At least one source file is required.", nameof(req));

        // Reject duplicate name early — the unique index also enforces it,
        // but a clean error code beats a 500 with a constraint-violation
        // message bubbling up to the UI.
        if (await _db.Corpora.AnyAsync(c => c.Name == req.Name, ct))
            throw new InvalidOperationException(
                $"A corpus named '{req.Name}' already exists. Choose a different name.");

        var now = DateTimeOffset.UtcNow;
        var corpusId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        // 1) Persist corpus + version in INGESTING state so partial failures
        //    leave a debuggable row instead of an orphaned ghost.
        var corpus = new Corpus
        {
            Id = corpusId,
            Name = req.Name,
            SourceType = req.SourceType,
            SourceUrl = req.SourceUrl,
            Branch = req.Branch,
            SourceRoot = req.SourceRoot,
            State = "INGESTING",
            FileCount = req.Files.Count,
            TotalLoc = 0,
            OwnerId = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var version = new SourceVersion
        {
            Id = versionId,
            CorpusId = corpusId,
            IngestedAt = now,
            IngestedBy = null,
            GitCommitHash = null,
            FileManifestBlobUri = $"minio://{_storage.Buckets.Sources}/{versionId}/manifest.json",
        };
        _db.Corpora.Add(corpus);
        _db.SourceVersions.Add(version);
        corpus.LatestVersionId = versionId;
        await _db.SaveChangesAsync(ct);

        // 2) Upload each file and parse. Collected warnings surface to UI;
        //    a single parse failure does NOT abort the whole corpus.
        var warnings = new List<string>();
        var totalLoc = 0;
        var totalSubs = 0;
        var fileEntities = new List<SourceFile>();

        try
        {
            foreach (var f in req.Files)
            {
                ct.ThrowIfCancellationRequested();

                var content = NormaliseNewlines(f.Content);
                var sha = Sha256(content);
                var blobKey = $"{versionId}/{f.RelativePath}";
                var blobUri = await _blob.PutTextAsync(
                    _storage.Buckets.Sources,
                    blobKey,
                    content,
                    "text/x-fortran",
                    ct);

                var lineCount = CountLines(content);
                totalLoc += lineCount;

                var fileRow = new SourceFile
                {
                    Id = Guid.NewGuid(),
                    SourceVersionId = versionId,
                    RelativePath = f.RelativePath,
                    FileHash = sha,
                    LineCount = lineCount,
                    BlobUri = blobUri,
                };
                _db.SourceFiles.Add(fileRow);
                fileEntities.Add(fileRow);
            }

            // Commit files before we start parsing — keeps blob + DB in sync
            // even if a later parse RPC fails.
            corpus.State = "PARSING";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            corpus.TotalLoc = totalLoc;
            await _db.SaveChangesAsync(ct);

            foreach (var (incoming, fileRow) in req.Files.Zip(fileEntities))
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await _parser.ParseAsync(
                    fileRow.RelativePath,
                    NormaliseNewlines(incoming.Content),
                    form: null,
                    ct: ct);

                foreach (var w in outcome.Warnings)
                    warnings.Add($"{fileRow.RelativePath}: {w}");

                foreach (var sub in outcome.Subroutines)
                {
                    var commonRefs = JsonSerializer.Serialize(sub.CommonBlockRefs);
                    var calls = JsonSerializer.Serialize(sub.CalledSubroutines);
                    _db.Subroutines.Add(new Subroutine
                    {
                        Id = Guid.NewGuid(),
                        SourceFileId = fileRow.Id,
                        Name = sub.Name,
                        Signature = sub.Signature,
                        LineStart = sub.LineStart,
                        LineEnd = sub.LineEnd,
                        CommonBlockRefs = JsonDocument.Parse(commonRefs),
                        CalledSubroutines = JsonDocument.Parse(calls),
                        IoPatterns = null,  // C.2 doesn't infer IO patterns yet
                        State = "PARSED",
                    });
                    totalSubs++;
                }
            }

            corpus.State = "PARSED";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync("corpus.ingested", "corpus", corpus.Id, actor: _persona,
                payload: new
                {
                    name = corpus.Name,
                    sourceType = corpus.SourceType,
                    sourceUrl = corpus.SourceUrl,
                    branch = corpus.Branch,
                    fileCount = corpus.FileCount,
                    totalLoc = corpus.TotalLoc,
                    subroutineCount = totalSubs,
                    warnings = warnings.Count,
                },
                ct: ct);

            _logger.LogInformation(
                "Ingest complete: corpus={Corpus} files={Files} loc={Loc} subs={Subs} warnings={W}",
                corpus.Id, corpus.FileCount, totalLoc, totalSubs, warnings.Count);

            return new IngestResult(
                CorpusId: corpus.Id,
                State: corpus.State,
                FileCount: corpus.FileCount,
                TotalLoc: corpus.TotalLoc,
                SubroutineCount: totalSubs,
                Warnings: warnings,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingest failed for corpus {Corpus}", corpus.Id);

            // Mark FAILED but keep the rows so the user can see what happened.
            corpus.State = "FAILED";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Failed to mark corpus FAILED"); }

            await _audit.LogAsync("corpus.ingest_failed", "corpus", corpus.Id, actor: _persona,
                payload: new { error = ex.Message }, ct: ct);

            return new IngestResult(
                CorpusId: corpus.Id,
                State: corpus.State,
                FileCount: corpus.FileCount,
                TotalLoc: totalLoc,
                SubroutineCount: totalSubs,
                Warnings: warnings,
                ErrorMessage: ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Re-sync (Phase C.3)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ingest a new <see cref="SourceVersion"/> for an existing corpus and
    /// reconcile prior specs:
    ///
    ///   - unchanged subroutine (path + name + file_hash identical) →
    ///     carry the spec, claim reviews, and signature forward onto the
    ///     new subroutine row. The new spec's <c>PreviousSpecId</c> points
    ///     at the prior spec for audit lineage.
    ///
    ///   - changed subroutine (path + name match but file_hash differs) OR
    ///     removed subroutine (no match in the new version) → mark the
    ///     prior spec <c>state = SUPERSEDED</c> and emit a
    ///     <c>spec.superseded</c> audit event.
    ///
    /// Drafts and in-review specs (non-SIGNED) on changed/removed routines
    /// are also marked SUPERSEDED; un-signed work doesn't survive a re-sync.
    /// </summary>
    public async Task<ReingestResult> ReingestAsync(ReingestRequest req, CancellationToken ct = default)
    {
        if (req.Files.Count == 0)
            throw new ArgumentException("At least one source file is required.", nameof(req));

        var corpus = await _db.Corpora
            .Include(c => c.Versions)
                .ThenInclude(v => v.Files)
                    .ThenInclude(f => f.Subroutines)
            .FirstOrDefaultAsync(c => c.Id == req.CorpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {req.CorpusId} not found.");

        // Build the prior-version index: (relativePath, subroutineName) → (subroutine, file).
        // Phase C.3 considers only the current LatestVersion as the baseline
        // — earlier versions stay archived; supersession lineage is a chain
        // of at most one hop per re-sync.
        var priorVersion = corpus.Versions
            .OrderByDescending(v => v.IngestedAt)
            .FirstOrDefault();

        var priorIndex = new Dictionary<(string Path, string Name), (Subroutine Sub, SourceFile File)>();
        if (priorVersion is not null)
        {
            foreach (var f in priorVersion.Files)
                foreach (var s in f.Subroutines)
                    priorIndex[(f.RelativePath, s.Name)] = (s, f);
        }

        // Load all prior specs that could be affected.
        var priorSubroutineIds = priorIndex.Values.Select(v => v.Sub.Id).ToHashSet();
        var priorSpecs = await _db.Specs
            .Where(s => priorSubroutineIds.Contains(s.SubroutineId))
            .ToDictionaryAsync(s => s.SubroutineId, ct);

        var now = DateTimeOffset.UtcNow;
        var newVersionId = Guid.NewGuid();
        var newVersion = new SourceVersion
        {
            Id = newVersionId,
            CorpusId = corpus.Id,
            GitCommitHash = req.GitCommitHash,
            IngestedAt = now,
            IngestedBy = null,
            FileManifestBlobUri = $"minio://{_storage.Buckets.Sources}/{newVersionId}/manifest.json",
        };
        _db.SourceVersions.Add(newVersion);

        // Snapshot fields onto corpus while we work; flip LatestVersionId at the end.
        corpus.State = "INGESTING";
        if (!string.IsNullOrWhiteSpace(req.SourceUrl)) corpus.SourceUrl = req.SourceUrl;
        if (!string.IsNullOrWhiteSpace(req.Branch)) corpus.Branch = req.Branch;
        if (!string.IsNullOrWhiteSpace(req.SourceRoot)) corpus.SourceRoot = req.SourceRoot;
        corpus.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var warnings = new List<string>();
        var totalLoc = 0;
        var totalSubs = 0;
        var carriedForward = 0;
        var superseded = 0;
        var newFileRows = new List<(IncomingFile Incoming, SourceFile File)>();

        try
        {
            // Upload + persist files first.
            foreach (var f in req.Files)
            {
                ct.ThrowIfCancellationRequested();
                var content = NormaliseNewlines(f.Content);
                var sha = Sha256(content);
                var blobKey = $"{newVersionId}/{f.RelativePath}";
                var blobUri = await _blob.PutTextAsync(_storage.Buckets.Sources, blobKey, content, "text/x-fortran", ct);
                var lineCount = CountLines(content);
                totalLoc += lineCount;

                var fileRow = new SourceFile
                {
                    Id = Guid.NewGuid(),
                    SourceVersionId = newVersionId,
                    RelativePath = f.RelativePath,
                    FileHash = sha,
                    LineCount = lineCount,
                    BlobUri = blobUri,
                };
                _db.SourceFiles.Add(fileRow);
                newFileRows.Add((f, fileRow));
            }

            corpus.State = "PARSING";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            corpus.FileCount = req.Files.Count;
            corpus.TotalLoc = totalLoc;
            await _db.SaveChangesAsync(ct);

            // Parse + reconcile each new subroutine.
            var seenPriorKeys = new HashSet<(string, string)>();
            foreach (var (incoming, fileRow) in newFileRows)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await _parser.ParseAsync(
                    fileRow.RelativePath,
                    NormaliseNewlines(incoming.Content),
                    form: null,
                    ct: ct);

                foreach (var w in outcome.Warnings)
                    warnings.Add($"{fileRow.RelativePath}: {w}");

                foreach (var sub in outcome.Subroutines)
                {
                    var newSubId = Guid.NewGuid();
                    var commonRefs = JsonSerializer.Serialize(sub.CommonBlockRefs);
                    var calls = JsonSerializer.Serialize(sub.CalledSubroutines);
                    var newSub = new Subroutine
                    {
                        Id = newSubId,
                        SourceFileId = fileRow.Id,
                        Name = sub.Name,
                        Signature = sub.Signature,
                        LineStart = sub.LineStart,
                        LineEnd = sub.LineEnd,
                        CommonBlockRefs = JsonDocument.Parse(commonRefs),
                        CalledSubroutines = JsonDocument.Parse(calls),
                        IoPatterns = null,
                        State = "PARSED",
                    };
                    _db.Subroutines.Add(newSub);
                    totalSubs++;

                    var key = (fileRow.RelativePath, sub.Name);
                    seenPriorKeys.Add(key);

                    if (!priorIndex.TryGetValue(key, out var prior)) continue;
                    if (!priorSpecs.TryGetValue(prior.Sub.Id, out var priorSpec)) continue;

                    var unchanged = prior.File.FileHash == fileRow.FileHash;
                    if (unchanged)
                    {
                        // Carry forward: clone spec + claim reviews + signature.
                        var carried = await CarrySpecForwardAsync(priorSpec, newSubId, newVersionId, ct);
                        // Match the new subroutine's lifecycle state to where the
                        // spec sits. Without this, a SIGNED spec would dangle off
                        // a PARSED-badged subroutine in the corpus detail UI.
                        newSub.State = MapSpecStateToSubroutineState(priorSpec.State, prior.Sub.State);
                        carriedForward++;
                        await _audit.LogAsync(
                            "spec.carried_forward", "spec", carried.Id, actor: _persona,
                            payload: new
                            {
                                previousSpecId = priorSpec.Id,
                                subroutine = sub.Name,
                                relativePath = fileRow.RelativePath,
                                fileHash = fileRow.FileHash,
                                state = carried.State,
                            },
                            ct: ct);
                    }
                    else
                    {
                        priorSpec.State = "SUPERSEDED";
                        priorSpec.UpdatedAt = DateTimeOffset.UtcNow;
                        superseded++;
                        await _audit.LogAsync(
                            "spec.superseded", "spec", priorSpec.Id, actor: _persona,
                            payload: new
                            {
                                reason = "source_changed",
                                subroutine = sub.Name,
                                relativePath = fileRow.RelativePath,
                                priorFileHash = prior.File.FileHash,
                                newFileHash = fileRow.FileHash,
                                priorState = priorSpec.State,
                            },
                            ct: ct);
                    }
                }
            }

            // Any prior-version subroutines that didn't reappear in the new
            // version → their specs are superseded with reason "removed".
            foreach (var ((path, name), prior) in priorIndex)
            {
                if (seenPriorKeys.Contains((path, name))) continue;
                if (!priorSpecs.TryGetValue(prior.Sub.Id, out var priorSpec)) continue;
                if (priorSpec.State == "SUPERSEDED") continue;

                priorSpec.State = "SUPERSEDED";
                priorSpec.UpdatedAt = DateTimeOffset.UtcNow;
                superseded++;
                await _audit.LogAsync(
                    "spec.superseded", "spec", priorSpec.Id, actor: _persona,
                    payload: new
                    {
                        reason = "subroutine_removed",
                        subroutine = name,
                        relativePath = path,
                        priorState = priorSpec.State,
                    },
                    ct: ct);
            }

            corpus.LatestVersionId = newVersionId;
            corpus.State = "PARSED";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(
                "corpus.reingested", "corpus", corpus.Id, actor: _persona,
                payload: new
                {
                    newVersionId,
                    sourceUrl = corpus.SourceUrl,
                    branch = corpus.Branch,
                    fileCount = corpus.FileCount,
                    totalLoc = corpus.TotalLoc,
                    subroutineCount = totalSubs,
                    carriedForward,
                    superseded,
                    warnings = warnings.Count,
                },
                ct: ct);

            _logger.LogInformation(
                "Re-sync complete: corpus={Corpus} files={Files} loc={Loc} subs={Subs} carried={C} superseded={S}",
                corpus.Id, corpus.FileCount, totalLoc, totalSubs, carriedForward, superseded);

            return new ReingestResult(
                CorpusId: corpus.Id,
                State: corpus.State,
                FileCount: corpus.FileCount,
                TotalLoc: corpus.TotalLoc,
                SubroutineCount: totalSubs,
                CarriedForwardCount: carriedForward,
                SupersededCount: superseded,
                Warnings: warnings,
                ErrorMessage: null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-sync failed for corpus {Corpus}", corpus.Id);
            corpus.State = "FAILED";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Failed to mark corpus FAILED"); }

            await _audit.LogAsync("corpus.reingest_failed", "corpus", corpus.Id, actor: _persona,
                payload: new { error = ex.Message }, ct: ct);

            return new ReingestResult(
                CorpusId: corpus.Id,
                State: corpus.State,
                FileCount: corpus.FileCount,
                TotalLoc: totalLoc,
                SubroutineCount: totalSubs,
                CarriedForwardCount: carriedForward,
                SupersededCount: superseded,
                Warnings: warnings,
                ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Deep-copy a spec onto a new subroutine row, preserving state,
    /// claim reviews, and the cryptographic signature (the signature is
    /// over canonical JSON, not the spec id, so it remains verifiable).
    /// Sets <c>PreviousSpecId</c> on the new row for audit lineage.
    /// </summary>
    private async Task<Spec> CarrySpecForwardAsync(
        Spec priorSpec, Guid newSubroutineId, Guid newSourceVersionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var newSpecId = Guid.NewGuid();

        // Re-parse the spec JSON so we get an independent JsonDocument we
        // can attach to a new entity without sharing buffers.
        var specJson = JsonDocument.Parse(priorSpec.SpecJson.RootElement.GetRawText());

        var newSpec = new Spec
        {
            Id = newSpecId,
            SubroutineId = newSubroutineId,
            SourceVersionId = newSourceVersionId,
            State = priorSpec.State,
            SpecJson = specJson,
            LlmCallId = priorSpec.LlmCallId,
            CreatedBy = priorSpec.CreatedBy,
            CreatedAt = priorSpec.CreatedAt,
            UpdatedAt = now,
            PreviousSpecId = priorSpec.Id,
        };
        _db.Specs.Add(newSpec);

        // Copy claim reviews to the new spec id.
        var priorReviews = await _db.ClaimReviews
            .Where(r => r.SpecId == priorSpec.Id)
            .AsNoTracking()
            .ToListAsync(ct);
        foreach (var r in priorReviews)
        {
            _db.ClaimReviews.Add(new ClaimReview
            {
                Id = Guid.NewGuid(),
                SpecId = newSpecId,
                ClaimPath = r.ClaimPath,
                Action = r.Action,
                Reason = r.Reason,
                EditedText = r.EditedText,
                ReviewerId = r.ReviewerId,
                ReviewedAt = r.ReviewedAt,
            });
        }

        // Copy the signature row if one exists. SpecCanonicalHash + the
        // signature bytes are over the canonical spec content, not the
        // spec id, so the proof remains valid against the new row.
        var priorSig = await _db.Signatures
            .Where(s => s.SpecId == priorSpec.Id)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        if (priorSig is not null)
        {
            _db.Signatures.Add(new Signature
            {
                Id = Guid.NewGuid(),
                SpecId = newSpecId,
                SignerId = priorSig.SignerId,
                SignerDisplay = priorSig.SignerDisplay,
                SignedAt = priorSig.SignedAt,
                SourceVersionHash = priorSig.SourceVersionHash,
                SpecCanonicalHash = priorSig.SpecCanonicalHash,
                SignatureBytes = priorSig.SignatureBytes.ToArray(),
                SignatureKeyId = priorSig.SignatureKeyId,
                Algorithm = priorSig.Algorithm,
                SignedBlobUri = priorSig.SignedBlobUri,
            });
        }

        return newSpec;
    }

    /// <summary>
    /// Pick the new subroutine's lifecycle state based on what the carried-
    /// forward spec is sitting at. Scaffolded subroutines stay scaffolded
    /// (the scaffold still references the prior spec id and is therefore
    /// still accessible via that id).
    /// </summary>
    private static string MapSpecStateToSubroutineState(string priorSpecState, string priorSubroutineState) =>
        priorSpecState switch
        {
            "SIGNED" => priorSubroutineState == "SCAFFOLDED" ? "SCAFFOLDED" : "SIGNED",
            "IN_REVIEW" => "IN_REVIEW",
            "DRAFT" => "DRAFT",
            _ => "PARSED",
        };

    private static string NormaliseNewlines(string content) =>
        content.Replace("\r\n", "\n").Replace("\r", "\n");

    private static int CountLines(string content) =>
        string.IsNullOrEmpty(content) ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);

    private static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
