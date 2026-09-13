using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Docs;
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
    private readonly DriftDetectionService _drift;
    private readonly ILogger<IngestPipeline> _logger;
    private readonly IHostApplicationLifetime? _lifetime;

    public IngestPipeline(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IFortranParserClient parser,
        IAuditLogger audit,
        DevPersonaContext persona,
        DriftDetectionService drift,
        ILogger<IngestPipeline> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _parser = parser;
        _audit = audit;
        _persona = persona;
        _drift = drift;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Once the files are accepted the work runs to completion even if the
    /// HTTP client goes away. Azure App Service cuts a request at 230 s and
    /// a whole-corpus parse can take longer; aborting with the client used
    /// to leave a half-built version behind that every "newest version"
    /// query then picked up as an empty corpus. Only application shutdown
    /// cancels an ingest.
    /// </summary>
    private CancellationToken WorkToken(CancellationToken requestToken)
        => _lifetime?.ApplicationStopping ?? requestToken;

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

    /// <summary>
    /// Parse every file of a version, in input order. A C++ corpus goes to
    /// the sidecar in one call so it can put the files on disk and resolve
    /// definitions, qualified callees and shared state across headers;
    /// everything else — and a C++ corpus too large for one message — is
    /// parsed file by file as before. Corpus-level warnings land in
    /// <paramref name="warnings"/> un-prefixed.
    /// </summary>
    private async Task<IReadOnlyList<ParseOutcome>> ParseFilesAsync(
        IReadOnlyList<IncomingFile> files,
        List<string> warnings,
        CancellationToken ct)
    {
        var totalBytes = files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Content));
        if (IngestParseRouting.UseCorpusMode(files.Select(f => f.RelativePath), totalBytes))
        {
            var corpus = await _parser.ParseCorpusAsync(
                files.Select(f => new CorpusFile(f.RelativePath, NormaliseNewlines(f.Content))).ToList(), ct);
            warnings.AddRange(corpus.Warnings);
            _logger.LogInformation(
                "Ingest: corpus parse of {Files} files ({Bytes} bytes), cross-file resolved={Resolved}",
                files.Count, totalBytes, corpus.CrossFileResolved);
            return corpus.Results;
        }

        if (files.Any(f => SourceLanguageDetector.FromFilename(f.RelativePath) == SourceLanguageDetector.Cpp))
        {
            warnings.Add(
                $"corpus is {totalBytes / (1024 * 1024)} MiB, above the " +
                $"{IngestParseRouting.CorpusParseMaxBytes / (1024 * 1024)} MiB whole-corpus parse limit — " +
                "C++ files parsed in isolation, cross-file calls unresolved");
        }

        var results = new List<ParseOutcome>(files.Count);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await _parser.ParseAsync(f.RelativePath, NormaliseNewlines(f.Content), form: null, ct: ct));
        }
        return results;
    }

    public async Task<IngestResult> IngestAsync(IngestRequest req, CancellationToken ct = default)
    {
        ct = WorkToken(ct);
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

            var outcomes = await ParseFilesAsync(req.Files, warnings, ct);
            for (var i = 0; i < fileEntities.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var fileRow = fileEntities[i];
                var outcome = outcomes[i];

                foreach (var w in outcome.Warnings)
                    warnings.Add($"{fileRow.RelativePath}: {w}");

                // Phase 5.2 — detect source language from the file's
                // extension and persist it on every parsed subroutine.
                // No silent default: the old fortran-f77 fallback stamped a
                // whole .NET corpus as Fortran (Adv-Connect), which then
                // routed extraction prompts for the wrong language. Upstream
                // extension filters should make null impossible — if it
                // happens anyway, skip the file's routines and say so.
                var sourceLanguage = SourceLanguageDetector.FromFilename(fileRow.RelativePath);
                if (sourceLanguage is null)
                {
                    warnings.Add(
                        $"{fileRow.RelativePath}: no language mapping for this extension — " +
                        $"routines skipped. Supported: {SourceLanguageDetector.SupportedLanguagesDescription()}");
                    _logger.LogWarning(
                        "Ingest: no language mapping for {Path}; skipping its {Count} routine(s)",
                        fileRow.RelativePath, outcome.Subroutines.Count);
                    continue;
                }

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
                        SourceLanguage = sourceLanguage,
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

            await _drift.MarkStaleAsync(corpus.Id, versionId, _persona, ct);

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
                ErrorMessage: ClientSafeErrorMessage(corpus.Id));
        }
    }

    /// <summary>
    /// A generic message for the API response / UI. The real exception (which for
    /// storage-layer failures can include request IDs, XML error bodies and other
    /// backend-internal detail) goes to <see cref="_logger"/> only, via the
    /// LogError call at the catch site — never to the client.
    /// </summary>
    private static string ClientSafeErrorMessage(Guid corpusId) =>
        $"Ingest failed unexpectedly. Check the server log for corpus {corpusId} for details.";

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
        ct = WorkToken(ct);

        await RemoveAbandonedVersionsAsync(req.CorpusId, ct);

        var corpus = await _db.Corpora
            .Include(c => c.Versions)
                .ThenInclude(v => v.Files)
                    .ThenInclude(f => f.Subroutines)
            .FirstOrDefaultAsync(c => c.Id == req.CorpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {req.CorpusId} not found.");

        // Build the prior-version index: (relativePath, subroutineName) → (subroutine, file).
        // The baseline is the version the corpus actually points at — the
        // one the funnel, the specs and the docs were built on — not merely
        // the newest row. Earlier versions stay archived; supersession
        // lineage is a chain of at most one hop per re-sync.
        // If the version the corpus points at holds no routines (a re-sync
        // whose parse came back empty), the last version that does is the
        // real baseline: that is where the specs live.
        var pointed = corpus.Versions.FirstOrDefault(v => v.Id == corpus.LatestVersionId);
        var priorVersion = pointed is not null && pointed.Files.Any(f => f.Subroutines.Count > 0)
            ? pointed
            : corpus.Versions
                .Where(v => v.Files.Any(f => f.Subroutines.Count > 0))
                .OrderByDescending(v => v.IngestedAt)
                .FirstOrDefault()
              ?? pointed
              ?? corpus.Versions.OrderByDescending(v => v.IngestedAt).FirstOrDefault();
        if (pointed is not null && priorVersion is not null && priorVersion.Id != pointed.Id)
        {
            _logger.LogWarning(
                "Re-sync: corpus {Corpus} points at version {Pointed} which has no routines; using {Baseline} as the baseline",
                corpus.Id, pointed.Id, priorVersion.Id);
        }

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
            var outcomes = await ParseFilesAsync(newFileRows.Select(r => r.Incoming).ToList(), warnings, ct);

            // A parse that failed outright — every file degraded by a parser
            // RPC failure, nothing found — must not become the version the
            // corpus points at: that would empty every view and orphan the
            // specs. Fail the re-sync instead; the rollback keeps the
            // previous version active.
            if (priorIndex.Count > 0
                && outcomes.All(o => o.Subroutines.Count == 0)
                && outcomes.Any(o => o.Warnings.Any(w => w.StartsWith("parser_rpc_failed", StringComparison.Ordinal))))
            {
                var reason = outcomes.SelectMany(o => o.Warnings).First(w => w.StartsWith("parser_rpc_failed", StringComparison.Ordinal));
                throw new InvalidOperationException($"Parser sidecar failed for the whole corpus: {reason}");
            }
            for (var i = 0; i < newFileRows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (incoming, fileRow) = newFileRows[i];
                var outcome = outcomes[i];

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
                        // A spec superseded by an earlier re-sync although its
                        // source never changed (that re-sync's parse came back
                        // empty, so every routine looked "removed") is still
                        // the spec for this exact source: restore the state
                        // the routine's own lifecycle still records.
                        if (priorSpec.State == "SUPERSEDED")
                        {
                            var restored = RestoreSpecStateFromSubroutine(prior.Sub.State);
                            priorSpec.State = restored;
                            priorSpec.UpdatedAt = DateTimeOffset.UtcNow;
                            await _audit.LogAsync(
                                "spec.restored", "spec", priorSpec.Id, actor: _persona,
                                payload: new
                                {
                                    reason = "source_unchanged_after_supersession",
                                    subroutine = sub.Name,
                                    relativePath = fileRow.RelativePath,
                                    fileHash = fileRow.FileHash,
                                    restoredState = restored,
                                },
                                ct: ct);
                        }

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
                        var priorState = priorSpec.State;
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
                                priorState,
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

                var removedState = priorSpec.State;
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
                        priorState = removedState,
                    },
                    ct: ct);
            }

            corpus.LatestVersionId = newVersionId;
            corpus.State = "PARSED";
            corpus.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            // The corpus now points at a real version; an empty one it may
            // have pointed at (a re-sync whose parse came back empty) has no
            // reason to stay.
            await DeleteEmptyVersionsAsync(corpus.Id, priorVersion?.IngestedAt, keepId: newVersionId, ct);

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

            await _drift.MarkStaleAsync(corpus.Id, newVersionId, _persona, ct);

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
        catch (OperationCanceledException)
        {
            // Application shutdown: leave no half-built version behind.
            await RollbackNewVersionAsync(corpus.Id, newVersionId, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-sync failed for corpus {Corpus}; rolling back version {Version}", corpus.Id, newVersionId);
            await RollbackNewVersionAsync(corpus.Id, newVersionId, ct);
            corpus.State = "FAILED";

            await _audit.LogAsync("corpus.reingest_failed", "corpus", corpus.Id, actor: _persona,
                payload: new { error = ex.Message, rolledBackVersionId = newVersionId }, ct: ct);

            return new ReingestResult(
                CorpusId: corpus.Id,
                State: corpus.State,
                FileCount: corpus.FileCount,
                TotalLoc: totalLoc,
                SubroutineCount: totalSubs,
                CarriedForwardCount: carriedForward,
                SupersededCount: superseded,
                Warnings: warnings,
                ErrorMessage: ClientSafeErrorMessage(corpus.Id));
        }
    }

    /// <summary>
    /// A re-sync that died mid-way (client gone, process restarted) used to
    /// leave its half-built version behind: newest by IngestedAt, no
    /// routines, never made LatestVersionId. Everything that picks "the
    /// newest version" (dependency graph, corpus list, harmonisation) then
    /// saw an empty corpus while the funnel, which follows LatestVersionId,
    /// still saw the old one. Remove such leftovers before building on the
    /// real baseline. Only versions at or after the baseline are touched;
    /// older archived versions are never deleted.
    /// </summary>
    private async Task RemoveAbandonedVersionsAsync(Guid corpusId, CancellationToken ct)
    {
        var latestId = await _db.Corpora
            .Where(c => c.Id == corpusId)
            .Select(c => c.LatestVersionId)
            .FirstOrDefaultAsync(ct);
        DateTimeOffset? baselineAt = latestId is null
            ? null
            : await _db.SourceVersions
                .Where(v => v.Id == latestId)
                .Select(v => (DateTimeOffset?)v.IngestedAt)
                .FirstOrDefaultAsync(ct);

        await DeleteEmptyVersionsAsync(corpusId, baselineAt, keepId: latestId, ct);
    }

    /// <summary>
    /// Delete every version of the corpus that holds no routines, was
    /// ingested at or after <paramref name="sinceInclusive"/> (null = any
    /// time) and is not <paramref name="keepId"/>. Used before a re-sync
    /// (leftovers of interrupted runs) and after a successful one (an empty
    /// version the corpus used to point at, now superseded by a real one).
    /// </summary>
    private async Task DeleteEmptyVersionsAsync(Guid corpusId, DateTimeOffset? sinceInclusive, Guid? keepId, CancellationToken ct)
    {
        var empty = await _db.SourceVersions
            .Where(v => v.CorpusId == corpusId
                        && v.Id != keepId
                        && (sinceInclusive == null || v.IngestedAt >= sinceInclusive)
                        && !v.Files.Any(f => f.Subroutines.Any()))
            .Select(v => new { v.Id, v.IngestedAt })
            .ToListAsync(ct);

        foreach (var v in empty)
        {
            await DeleteVersionRowsAsync(v.Id, ct);
            _logger.LogWarning(
                "Re-sync: removed empty version {Version} of corpus {Corpus} (ingested {At:u}, no routines — left by an interrupted or failed re-sync)",
                v.Id, corpusId, v.IngestedAt);
        }
    }

    /// <summary>
    /// Undo a re-sync that failed after its files were saved: drop every
    /// pending change (new routines, carried specs, supersession marks) and
    /// the rows already written for the new version, and mark the corpus
    /// FAILED. LatestVersionId is untouched, so the previous version stays
    /// the one every view reads.
    /// </summary>
    private async Task RollbackNewVersionAsync(Guid corpusId, Guid newVersionId, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();
            await DeleteVersionRowsAsync(newVersionId, ct);
            await _db.Corpora
                .Where(c => c.Id == corpusId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.State, "FAILED")
                    .SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback of version {Version} for corpus {Corpus} failed", newVersionId, corpusId);
        }
    }

    private async Task DeleteVersionRowsAsync(Guid versionId, CancellationToken ct)
    {
        var fileIds = _db.SourceFiles.Where(f => f.SourceVersionId == versionId).Select(f => f.Id);
        await _db.Subroutines.Where(s => fileIds.Contains(s.SourceFileId)).ExecuteDeleteAsync(ct);
        await _db.SourceFiles.Where(f => f.SourceVersionId == versionId).ExecuteDeleteAsync(ct);
        await _db.SourceVersions.Where(v => v.Id == versionId).ExecuteDeleteAsync(ct);
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
    /// <summary>
    /// Inverse of <see cref="MapSpecStateToSubroutineState"/> for a spec whose
    /// own state was lost to a wrongful supersession: the routine's lifecycle
    /// state still says where the spec sat.
    /// </summary>
    private static string RestoreSpecStateFromSubroutine(string subroutineState) =>
        subroutineState switch
        {
            "IN_REVIEW" => "IN_REVIEW",
            "SIGNED" or "SCAFFOLDED" or "COMMITTED" => "SIGNED",
            _ => "DRAFT",
        };

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
