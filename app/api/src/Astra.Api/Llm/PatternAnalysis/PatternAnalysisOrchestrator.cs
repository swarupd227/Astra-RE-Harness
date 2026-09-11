using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// Phase 12.0 — Two-stage pattern-analysis pass over a corpus:
///
///   Stage 1 "extract": bulk-run the EXISTING per-subroutine
///     <see cref="ExtractionPipeline"/> over every un-extracted subroutine
///     in the corpus's latest version, bounded concurrency, background job
///     (mirrors <c>Docs/RoutineSummaryPipeline</c>'s loop).
///   Stage 2 "cluster": bucket every resulting spec's claims by kind
///     (<see cref="Validation.ClaimKindBucketer"/>), then send the corpus
///     to the LLM asking it to group routines that share one real
///     behavioural pattern, producing <see cref="PatternCluster"/> rows
///     with a suggested archetype name per cluster. When the whole corpus
///     fits in one call (mirrors <c>HarmonisationPipeline</c>) that's a
///     single request; when it doesn't (very large corpora — see
///     <see cref="BuildBatches"/>), it's several independently-clustered
///     batches followed by one reconciliation call that merges clusters
///     representing the same pattern across batches.
///
/// One call per batch (not one per bucket) so the model can split a
/// bucket whose members only superficially share claim kinds, or merge
/// across buckets when the same real idiom happens to produce slightly
/// different claim sets — decisions that require seeing as much of the
/// corpus at once as the context budget allows.
/// </summary>
public sealed class PatternAnalysisOrchestrator
{
    /// <summary>Attempts per routine before giving up. Attempt 1 may use
    /// the trivial-tier model; later attempts always use the default one,
    /// so a weaker model's malformed output self-corrects.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Write a progress row every N completions — often enough to
    /// watch, rare enough not to hammer Postgres from 8 threads.</summary>
    private const int ProgressUpdateEvery = 10;

    /// <summary>Output ceiling for trivial routines. A one-line accessor
    /// that wants more than this has gone wrong, and capping it bounds the
    /// damage rather than letting it generate for 80 seconds.</summary>
    private const int TrivialMaxOutputTokens = 4096;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Qualified ("OrderService.getId") and bare ("GET_ID") accessor shapes.
    private static readonly Regex AccessorNameRegex =
        new(@"(^|\.)(get|set|is|has)[A-Z_]", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatternAnalysisOrchestrator> _logger;
    private readonly Astra.Api.Docs.DocRunLogger _runLogger;
    private readonly int _maxConcurrency;
    private readonly string _trivialModel;

    public PatternAnalysisOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<PatternAnalysisOrchestrator> logger,
        Astra.Api.Docs.DocRunLogger runLogger,
        IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _runLogger = runLogger;
        // Was a hard-coded 4. Wall-clock on a bulk pass is linear in this.
        _maxConcurrency = Math.Clamp(cfg.GetValue("Llm:BulkExtractConcurrency", 8), 1, 32);
        // Trivial routines are the bulk of a bean-style corpus and their
        // specs are short; a fast small model turns 80s of generation into
        // a few. Set to the default model to disable tiering entirely.
        _trivialModel = cfg.GetValue<string>("Llm:Anthropic:TrivialModel")
                        ?? "claude-haiku-4-5-20251001";
    }

    /// <summary>
    /// Short accessor-shaped members get the cheap tier. Deliberately
    /// conservative: anything that isn't obviously boilerplate keeps the
    /// full treatment, because a wrong call here costs spec quality.
    /// </summary>
    private static bool IsTrivial(Subroutine s)
    {
        var loc = s.LineEnd - s.LineStart + 1;
        if (loc <= 3) return true;
        return loc <= 8 && AccessorNameRegex.IsMatch(s.Name);
    }

    public async Task<Guid> StartAsync(Guid corpusId, bool force, string? triggeredBy, CancellationToken ct)
    {
        Guid runId;
        Guid sourceVersionId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct)
                ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");
            if (corpus.LatestVersionId is null)
                throw new InvalidOperationException($"Corpus {corpus.Name} has no ingested version.");
            sourceVersionId = corpus.LatestVersionId.Value;

            var run = new PatternAnalysisRun
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                StagesRequested = "extract,cluster",
                State = "QUEUED",
                Summary = "Queued",
                TriggeredBy = triggeredBy,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.PatternAnalysisRuns.Add(run);
            await db.SaveChangesAsync(ct);
            runId = run.Id;
        }

        // Fire-and-forget: the request scope dies the moment the endpoint
        // responds, so the worker creates its own scopes throughout (same
        // pattern as DocsGenerationOrchestrator / RoutineSummaryPipeline).
        _ = Task.Run(() => RunPipelineAsync(runId, corpusId, sourceVersionId, force));
        return runId;
    }

    private async Task RunPipelineAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force)
    {
        var ct = CancellationToken.None;
        try
        {
            await UpdateAsync(runId, "RUNNING", "Stage 1/2: bulk extraction", null);
            var extractResult = await ExtractAllAsync(runId, corpusId, sourceVersionId, force, ct);
            await UpdateAsync(runId, "RUNNING",
                $"Extraction: {extractResult.Succeeded} succeeded, {extractResult.Failed} failed, " +
                $"{extractResult.Skipped} already-extracted. Stage 2/2: clustering",
                new Dictionary<string, object?> { ["extract"] = extractResult });

            var clusterResult = await ClusterAsync(runId, corpusId, sourceVersionId, ct);

            var state = extractResult.Succeeded == 0 && extractResult.Failed > 0
                ? "FAILED"
                : (extractResult.Failed > 0 || clusterResult.FailedBatchCount > 0 ? "PARTIAL" : "SUCCEEDED");
            var summary =
                $"{extractResult.Succeeded} extracted ({extractResult.Failed} failed, " +
                $"{extractResult.Skipped} already-extracted) → {clusterResult.ClusterCount} cluster(s) " +
                $"across {clusterResult.SubroutineCount} routine(s)" +
                (clusterResult.BatchCount > 1
                    ? $", clustered in {clusterResult.BatchCount} batches + reconciliation"
                    : "") +
                (clusterResult.FailedBatchCount > 0
                    ? $" ({clusterResult.FailedBatchCount} batch(es) failed — their routines are recorded as unclassified)"
                    : "") +
                ".";
            await UpdateAsync(runId, state, summary,
                new Dictionary<string, object?> { ["extract"] = extractResult, ["cluster"] = clusterResult },
                completed: true);
            _runLogger.Log(runId, summary);
            _runLogger.Complete(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pattern analysis run {RunId} crashed", runId);
            _runLogger.Log(runId, $"Run failed: {ex.Message}");
            _runLogger.Complete(runId);
            await FailAsync(runId, ex.Message);
        }
    }

    // ── Stage 1: bulk extraction ────────────────────────────────────────

    private sealed record ExtractStageResult(int Succeeded, int Failed, int Skipped);

    private async Task<ExtractStageResult> ExtractAllAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        List<Subroutine> todo;
        int totalInVersion;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var subs = await db.Subroutines
                .Include(s => s.SourceFile)
                .AsNoTracking()
                .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
                .OrderBy(s => s.SourceFile!.RelativePath).ThenBy(s => s.LineStart)
                .ToListAsync(ct);
            totalInVersion = subs.Count;

            // Never re-extract IN_REVIEW/SIGNED — that would clobber
            // SME-reviewed state. PARSED (never extracted) is always
            // eligible; DRAFT (extracted but not yet routed to review) is
            // only re-run under force=true.
            todo = subs.Where(s => s.State == "PARSED" || (force && s.State == "DRAFT")).ToList();
        }

        var skipped = totalInVersion - todo.Count;
        if (todo.Count == 0)
            return new ExtractStageResult(0, 0, skipped);

        var trivialCount = todo.Count(IsTrivial);
        _runLogger.Log(runId,
            $"Extracting {todo.Count} routine(s) at concurrency {_maxConcurrency} " +
            $"— {trivialCount} trivial → {_trivialModel}, {todo.Count - trivialCount} full-tier. " +
            $"{skipped} already extracted.");

        var sem = new SemaphoreSlim(_maxConcurrency);
        var completed = 0;
        var failedCount = 0;
        var results = await Task.WhenAll(todo.Select(async sub =>
        {
            var ok = await ExtractOneAsync(runId, sub, sem, ct);
            if (!ok) Interlocked.Increment(ref failedCount);
            var n = Interlocked.Increment(ref completed);
            _runLogger.Log(runId, $"[{n}/{todo.Count}] {(ok ? "ok  " : "FAIL")} {sub.Name}");
            // Progress is the difference between "slow" and "hung" to the
            // person watching; the old run summary never moved for hours.
            if (n % ProgressUpdateEvery == 0 || n == todo.Count)
            {
                await UpdateAsync(runId, "RUNNING",
                    $"Stage 1/2: extracted {n}/{todo.Count} routine(s)" +
                    $"{(Volatile.Read(ref failedCount) > 0 ? $", {Volatile.Read(ref failedCount)} failed" : "")}",
                    null);
            }
            return ok;
        }));
        var succeeded = results.Count(r => r);
        return new ExtractStageResult(succeeded, results.Length - succeeded, skipped);
    }

    private async Task<bool> ExtractOneAsync(Guid runId, Subroutine sub, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var trivial = IsTrivial(sub);
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // Only the first attempt uses the cheap tier: if a smaller
                // model returns malformed or truncated output, the retry
                // escalates to the default model rather than repeating it.
                var useTrivialTier = trivial && attempt == 1;
                var tuning = new ExtractionTuning(
                    ModelOverride: useTrivialTier ? _trivialModel : null,
                    MaxOutputTokensOverride: useTrivialTier ? TrivialMaxOutputTokens : null,
                    BulkMode: true);

                var (ok, retryable, code, error) = await RunOnceAsync(sub, tuning, ct);
                if (ok) return true;

                // A failed attempt can leave the row parked in EXTRACTING,
                // where the state guard would refuse it forever after.
                await ResetIfStuckAsync(sub.Id, ct);

                // Infrastructure failures (429, transport, dropped stream)
                // clear on their own, so they get the full attempt budget.
                // A model that generated for 80 seconds and produced
                // unusable output will usually do it again — that gets one
                // retry, not two, because each costs a full generation.
                var attemptBudget = IsInfrastructureError(code) ? MaxAttempts : 2;
                var lastAttempt = attempt >= attemptBudget;
                if (lastAttempt || (!retryable && !useTrivialTier))
                {
                    _logger.LogWarning(
                        "Bulk extraction failed for {Sub} after {Attempts} attempt(s): {Err}",
                        sub.Name, attempt, error);
                    return false;
                }
                // 2s, then 6s. Anthropic 429s clear inside this window.
                await Task.Delay(TimeSpan.FromSeconds(attempt * attempt * 2), ct);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk extraction threw for {Sub}", sub.Name);
            await ResetIfStuckAsync(sub.Id, CancellationToken.None);
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<(bool Ok, bool Retryable, string? Code, string? Error)> RunOnceAsync(
        Subroutine sub, ExtractionTuning tuning, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ExtractionPipeline>();
        var ok = false;
        var retryable = false;
        string? code = null;
        string? error = null;
        await foreach (var evt in pipeline.RunAsync(sub.Id, ct, tuning))
        {
            if (evt.Type == "done")
            {
                ok = true;
            }
            else if (evt.Type == "error")
            {
                error = JsonSerializer.Serialize(evt.Data);
                (retryable, code) = ReadError(evt.Data);
            }
        }
        return (ok, retryable, code, error);
    }

    /// <summary>The provider already classifies 429/5xx/transport as
    /// retryable on the error event; nothing acted on it until now.</summary>
    private static (bool Retryable, string? Code) ReadError(object? data)
    {
        if (data is null) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(data));
            var root = doc.RootElement;
            var retryable = root.TryGetProperty("retryable", out var r)
                            && r.ValueKind == JsonValueKind.True;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            return (retryable, code);
        }
        catch { return (false, null); }
    }

    /// <summary>
    /// True for failures that come from the network or the service rather
    /// than from what the model produced. Only these are worth the full
    /// attempt budget — a bad generation repeats.
    /// </summary>
    private static bool IsInfrastructureError(string? code) => code is
        "provider.transport" or
        "provider.rate_limited" or
        "provider.stream_error";

    /// <summary>
    /// Return a routine parked in EXTRACTING to a state the pipeline will
    /// accept again — DRAFT when a spec already exists, else PARSED.
    /// </summary>
    private async Task ResetIfStuckAsync(Guid subroutineId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Subroutines.FirstOrDefaultAsync(s => s.Id == subroutineId, ct);
            if (row is null || row.State != "EXTRACTING") return;
            var hasSpec = await db.Specs.AnyAsync(sp => sp.SubroutineId == subroutineId, ct);
            row.State = hasSpec ? "DRAFT" : "PARSED";
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear EXTRACTING state for {Sub}", subroutineId);
        }
    }

    // ── Stage 2: claim-kind bucketing + LLM-judged clustering ───────────

    private sealed record ClusterStageResult(
        int SubroutineCount, int ClusterCount, int InputTokens, int OutputTokens, int BatchCount, int FailedBatchCount = 0);

    private async Task<ClusterStageResult> ClusterAsync(Guid runId, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prompts = scope.ServiceProvider.GetRequiredService<Prompts.PromptLibrary>();
        var provider = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
        var anthropicOpts = scope.ServiceProvider.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");

        var subroutines = await db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
            .ToListAsync(ct);
        var subroutineIds = subroutines.Select(s => s.Id).ToList();
        var nameById = subroutines.ToDictionary(s => s.Id, s => s.Name);

        // Latest spec per subroutine — this is a discovery pass, so DRAFT
        // specs (not yet routed to review) are included, unlike Harmonisation
        // which only looks at SIGNED specs.
        var specs = await db.Specs
            .AsNoTracking()
            .Where(sp => subroutineIds.Contains(sp.SubroutineId))
            .GroupBy(sp => sp.SubroutineId)
            .Select(g => g.OrderByDescending(sp => sp.UpdatedAt).First())
            .ToListAsync(ct);

        // Clustering is a discovery snapshot, not additive history — clear
        // this corpus's prior clusters before writing this run's.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM pattern_clusters WHERE corpus_id = {corpusId}", ct);

        if (specs.Count == 0)
            return new ClusterStageResult(0, 0, 0, 0, 0);

        var signatureBySubroutine = new Dictionary<Guid, string>();
        var digests = new List<RoutineDigest>();
        foreach (var spec in specs)
        {
            var buckets = Validation.ClaimKindBucketer.Bucket(spec.SpecJson.RootElement);
            var signature = Validation.ClaimKindBucketer.Signature(buckets);
            signatureBySubroutine[spec.SubroutineId] = signature;
            digests.Add(new RoutineDigest(
                spec.SubroutineId,
                nameById.TryGetValue(spec.SubroutineId, out var n) ? n : "(unknown)",
                signature,
                ReadSpecPurpose(spec.SpecJson.RootElement),
                buckets));
        }

        var batches = BuildBatches(digests);
        _logger.LogInformation(
            "Pattern clustering digest: corpus={CorpusId} routines={Count} batches={BatchCount}",
            corpusId, digests.Count, batches.Count);
        if (batches.Count > 1)
        {
            _runLogger.Log(runId,
                $"Clustering input ({digests.Count} routines) exceeds the single-call token budget even at " +
                $"the smallest digest tier — split into {batches.Count} batches, to be reconciled afterwards.");
        }

        var loaded = prompts.GetLatest("common", "dotnet8", "cluster-patterns")
            ?? throw new InvalidOperationException(
                "No cluster-patterns prompt registered (common/dotnet8/cluster-patterns).");

        var subroutineIdByIndex = digests.Select(d => d.SubroutineId).ToList();
        var specIdBySubroutine = specs.ToDictionary(s => s.SubroutineId, s => s.Id);

        var allClusters = new List<PatternCluster>();
        int totalInputTokens = 0, totalOutputTokens = 0;
        var failedBatches = 0;
        for (var b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            var batchNote = batches.Count > 1
                ? $"\nNOTE: This is batch {b + 1} of {batches.Count} — a subset of {digests.Count} total " +
                  "corpus routines, split across batches to stay within the model's context limit. Cluster " +
                  "ONLY the routines shown below; do not assume you are seeing the whole corpus, and do not " +
                  "reference routines outside this batch. A separate reconciliation pass will merge clusters " +
                  "from different batches that turn out to describe the same real pattern.\n"
                : "";

            var rendered = prompts.Render(loaded, new Dictionary<string, string?>
            {
                ["corpusName"] = corpus.Name,
                ["sourceVersionId"] = sourceVersionId.ToString(),
                ["subroutineCount"] = batch.Indices.Count.ToString(),
                ["entriesJson"] = batch.Json,
                ["batchNote"] = batchNote,
            });

            // A transient failure on one batch (e.g. a 429/500 from
            // Anthropic) must not throw away every other batch's already-
            // clustered results — same "don't discard completed work"
            // reasoning as the batching split itself. A failed batch's
            // routines fall through to the unclassified-singleton safety
            // net below instead of aborting the whole run.
            try
            {
                ClusterLlmResult llmResult;
                if (string.Equals(provider.Info.Name, "anthropic", StringComparison.OrdinalIgnoreCase))
                {
                    llmResult = await CallAnthropicAsync(httpFactory, anthropicOpts, rendered.System, rendered.User, ct);
                }
                else
                {
                    var batchSubroutineIds = batch.Indices.Select(i => digests[i].SubroutineId).ToHashSet();
                    llmResult = StubMockResult(
                        specs.Where(s => batchSubroutineIds.Contains(s.SubroutineId)).ToList(), signatureBySubroutine);
                }
                totalInputTokens += llmResult.InputTokens;
                totalOutputTokens += llmResult.OutputTokens;

                allClusters.AddRange(ParseClusters(
                    llmResult.RawJson, runId, corpusId, nameById, specIdBySubroutine, signatureBySubroutine,
                    subroutineIdByIndex));
            }
            catch (Exception ex)
            {
                failedBatches++;
                _logger.LogWarning(ex,
                    "Pattern clustering batch {Batch}/{Total} failed for run {RunId}; its {Count} routine(s) will be recorded as unclassified.",
                    b + 1, batches.Count, runId, batch.Indices.Count);
                _runLogger.Log(runId,
                    $"Batch {b + 1}/{batches.Count} clustering call failed ({ex.Message}); its {batch.Indices.Count} " +
                    "routine(s) will be recorded as unclassified rather than failing the whole run.");
            }

            if (batches.Count > 1)
            {
                await UpdateAsync(runId, "RUNNING",
                    $"Stage 2/2: clustered batch {b + 1}/{batches.Count} ({batch.Indices.Count} routines) — " +
                    $"{allClusters.Count} cluster(s) so far" +
                    (failedBatches > 0 ? $", {failedBatches} batch(es) failed" : ""),
                    null);
            }
        }

        if (batches.Count > 1)
        {
            try
            {
                allClusters = await ReconcileClustersAsync(
                    runId, corpus.Name, allClusters, provider, prompts, httpFactory, anthropicOpts, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Pattern-cluster reconciliation failed for run {RunId}; keeping {Count} per-batch clusters unmerged.",
                    runId, allClusters.Count);
                _runLogger.Log(runId,
                    $"Reconciliation pass failed ({ex.Message}); keeping all {allClusters.Count} per-batch clusters unmerged.");
            }
        }

        // Safety net: any subroutine no batch placed gets its own singleton
        // cluster rather than silently vanishing from the report.
        var placed = allClusters
            .SelectMany(c => ParseMemberIds(c.MemberSubroutineIdsJson))
            .ToHashSet();
        foreach (var spec in specs)
        {
            if (placed.Contains(spec.SubroutineId)) continue;
            var name = nameById.TryGetValue(spec.SubroutineId, out var n) ? n : spec.SubroutineId.ToString();
            allClusters.Add(new PatternCluster
            {
                Id = Guid.NewGuid(),
                PatternAnalysisRunId = runId,
                CorpusId = corpusId,
                ClaimKindSignature = signatureBySubroutine.TryGetValue(spec.SubroutineId, out var sig) ? sig : "",
                Label = $"Unclassified — {name}",
                SuggestedArchetypeName = "",
                Rationale = "The clustering pass did not explicitly place this routine; recorded as its own " +
                            "singleton so no routine silently drops out of the report.",
                MemberSubroutineIdsJson = JsonSerializer.Serialize(new[]
                {
                    new { subroutineId = spec.SubroutineId, subroutineName = name, specId = spec.Id },
                }),
                MemberCount = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.PatternClusters.AddRangeAsync(allClusters, ct);
        await db.SaveChangesAsync(ct);

        return new ClusterStageResult(specs.Count, allClusters.Count, totalInputTokens, totalOutputTokens, batches.Count, failedBatches);
    }

    // ── Clustering-input digest ─────────────────────────────────────────
    // Embedding every routine's full spec/v1 JSON in the single clustering
    // prompt scaled linearly with corpus size and blew the 200k-token
    // context on large corpora (EnvestNet: 450 routines ≈ 1.7M tokens →
    // Anthropic 400 "prompt is too long"). The clustering judge needs each
    // routine's claim FLAVOUR, not the whole spec, so send a compact digest
    // instead — sized to a budget by degrading through tiers: claim
    // excerpts → shorter excerpts → per-kind counts only. The largest tier
    // that fits wins.
    //
    // Phase 12.0.1: even counts-only doesn't fit every corpus in one call
    // (oatpp: 1815 routines still serialised past budget, and Anthropic
    // rejected the resulting 274k-token prompt outright — the previous
    // "send it anyway" fallback was actually a hard failure, discarding
    // Stage 1's completed extraction work). BuildBatches splits routines
    // across several counts-only-tier batches instead, each clustered
    // independently and merged by ReconcileClustersAsync afterwards.
    private const int MaxEntriesJsonChars = 450_000; // ≈150k tokens at ~3 chars/token

    private static readonly JsonSerializerOptions DigestJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record RoutineDigest(
        Guid SubroutineId,
        string Name,
        string Signature,
        string? Purpose,
        Dictionary<string, List<string>> Buckets);

    private sealed record ClusterBatch(string Json, string Tier, IReadOnlyList<int> Indices);

    private static readonly (string Name, int ClaimsPerKind, int ClaimChars, int PurposeChars)[] DigestTiers =
    {
        ("excerpts", 4, 200, 300),
        ("short-excerpts", 2, 110, 160),
        ("counts-only", 0, 0, 110),
    };

    /// <summary>Serialise the given digest indices at one tier. Entries carry
    /// a compact integer index `n` instead of the GUID; the model references
    /// routines by `n` in its output, which cuts the response to ~1/10th the
    /// tokens of full-GUID output (which ran past the HTTP timeout on
    /// EnvestNet). `n` is always the index into the FULL digest list, not the
    /// batch — so batches never need to remap indices in the model's reply.</summary>
    private static string BuildEntriesJsonAtTier(
        IReadOnlyList<RoutineDigest> digests, IReadOnlyList<int> indices,
        (string Name, int ClaimsPerKind, int ClaimChars, int PurposeChars) tier)
    {
        var entries = indices.Select(i =>
        {
            var d = digests[i];
            return new
            {
                n = i,
                subroutineName = d.Name,
                claimKindSignature = d.Signature,
                purpose = d.Purpose is null ? null : Truncate(d.Purpose, tier.PurposeChars),
                claimCounts = d.Buckets.Where(kv => kv.Value.Count > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Count),
                claims = tier.ClaimsPerKind == 0
                    ? null
                    : d.Buckets.Where(kv => kv.Value.Count > 0)
                        .ToDictionary(
                            kv => kv.Key,
                            kv => kv.Value.Take(tier.ClaimsPerKind)
                                .Select(text => Truncate(text, tier.ClaimChars))
                                .ToList()),
            };
        });
        return JsonSerializer.Serialize(entries, DigestJsonOpts);
    }

    /// <summary>Try every tier in order (richest first) for the given
    /// indices; returns the first that fits the budget, or the smallest
    /// tier's JSON with <c>Fits=false</c> if none does.</summary>
    private static (string Json, string Tier, bool Fits) BuildEntriesJsonFor(
        IReadOnlyList<RoutineDigest> digests, IReadOnlyList<int> indices)
    {
        var json = "";
        var tierName = "";
        foreach (var tier in DigestTiers)
        {
            tierName = tier.Name;
            json = BuildEntriesJsonAtTier(digests, indices, tier);
            if (json.Length <= MaxEntriesJsonChars) return (json, tierName, true);
        }
        return (json, tierName, false);
    }

    /// <summary>
    /// Split the corpus's routines into one or more batches, each of which
    /// fits <see cref="MaxEntriesJsonChars"/>. The common case — the whole
    /// corpus fits at some tier — returns a single batch with EXACTLY the
    /// same JSON <see cref="BuildEntriesJsonFor"/> would have produced
    /// before batching existed, so nothing changes for corpora that already
    /// worked. Only when even the smallest (counts-only) tier can't fit
    /// everything does this fall through to packing routines into several
    /// counts-only batches — a uniform tier across batches keeps quality
    /// consistent and keeps the packing pass a single O(n) sweep.
    /// </summary>
    private static List<ClusterBatch> BuildBatches(IReadOnlyList<RoutineDigest> digests)
    {
        var all = Enumerable.Range(0, digests.Count).ToList();
        var (wholeJson, wholeTier, fits) = BuildEntriesJsonFor(digests, all);
        if (fits) return new List<ClusterBatch> { new(wholeJson, wholeTier, all) };

        var countsOnlyTier = DigestTiers[^1];
        // Pre-measure each routine's own counts-only entry length once, so
        // packing is a single linear sweep instead of re-serialising the
        // whole growing batch on every routine (which would be O(n²) at
        // oatpp's scale).
        var itemLengths = all
            .Select(i => BuildEntriesJsonAtTier(digests, new[] { i }, countsOnlyTier).Length - 2) // strip "[" "]"
            .ToList();

        var batches = new List<ClusterBatch>();
        var current = new List<int>();
        var runningLength = 2; // "[" + "]"
        foreach (var i in all)
        {
            var addLength = itemLengths[i] + (current.Count > 0 ? 1 : 0); // +1 for the joining comma
            if (current.Count > 0 && runningLength + addLength > MaxEntriesJsonChars)
            {
                batches.Add(new ClusterBatch(
                    BuildEntriesJsonAtTier(digests, current, countsOnlyTier), countsOnlyTier.Name, current));
                current = new List<int>();
                runningLength = 2;
                addLength = itemLengths[i];
            }
            current.Add(i);
            runningLength += addLength;
        }
        if (current.Count > 0)
        {
            batches.Add(new ClusterBatch(
                BuildEntriesJsonAtTier(digests, current, countsOnlyTier), countsOnlyTier.Name, current));
        }
        return batches;
    }

    private static string? ReadSpecPurpose(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "purpose", "summary", "description" })
        {
            if (root.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } s)
            {
                return s;
            }
        }
        return null;
    }

    private async Task<ClusterLlmResult> CallAnthropicAsync(
        IHttpClientFactory httpFactory, AnthropicOptions opts, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        // Timeout comes from the named registration (30 min) — do not
        // re-set it here: a 10-minute override was silently reinstating the
        // 600s cap the registration exists to escape.
        var http = httpFactory.CreateClient("anthropic-cluster-patterns");

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = opts.Model,
            ["max_tokens"] = opts.MaxOutputTokens,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{opts.BaseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", opts.ApiKey);
        req.Headers.Add("anthropic-version", opts.ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic returned {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        int inT = 0, outT = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i)) inT = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o)) outT = o.GetInt32();
        }

        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }
        return new ClusterLlmResult(sb.ToString(), inT, outT);
    }

    /// <summary>
    /// Mock provider stub — groups purely by the deterministic claim-kind
    /// signature (no LLM judging) so the pipeline can be exercised without
    /// an API key. Honest about being a stub, matching HarmonisationPipeline's
    /// StubMockResult convention.
    /// </summary>
    private static ClusterLlmResult StubMockResult(IList<Spec> specs, Dictionary<Guid, string> signatureBySubroutine)
    {
        var bySignature = specs.GroupBy(s => signatureBySubroutine.TryGetValue(s.SubroutineId, out var sig) ? sig : "");
        var clusters = bySignature.Select((g, i) => new
        {
            label = $"Mock cluster {i + 1} — signature: {(string.IsNullOrEmpty(g.Key) ? "(none)" : g.Key)}",
            suggestedArchetypeName = $"canonical-mock-pattern-{i + 1}",
            rationale = "Mock provider — grouped by claim-kind signature only, no LLM judging performed. " +
                        "Set Llm:Provider=anthropic to run a real judged clustering pass.",
            memberSubroutineIds = g.Select(s => s.SubroutineId.ToString()).ToArray(),
        }).ToArray();

        var payload = new
        {
            summary = $"Mock clustering pass — {specs.Count} specs grouped into {clusters.Length} signature bucket(s).",
            clusters,
        };
        return new ClusterLlmResult(JsonSerializer.Serialize(payload, JsonOpts), 0, 0);
    }

    // ── Cross-batch reconciliation ──────────────────────────────────────
    // Each batch clusters its own routines with no visibility into any
    // other batch, so the same real pattern can legitimately surface twice
    // — once per batch. This pass sees only compact per-cluster summaries
    // (never the underlying routine digests, which keeps it cheap
    // regardless of how many batches Stage 2 needed) and decides which
    // cross-batch clusters should merge into one.

    private static readonly JsonSerializerOptions CaseInsensitiveOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record MemberEntry(Guid SubroutineId, string SubroutineName, Guid? SpecId);

    private static List<MemberEntry> ParseMemberEntries(string json)
    {
        try { return JsonSerializer.Deserialize<List<MemberEntry>>(json, CaseInsensitiveOpts) ?? new(); }
        catch { return new(); }
    }

    private async Task<List<PatternCluster>> ReconcileClustersAsync(
        Guid runId, string corpusName, List<PatternCluster> clusters, ILlmProvider provider,
        Prompts.PromptLibrary prompts, IHttpClientFactory httpFactory, AnthropicOptions anthropicOpts,
        CancellationToken ct)
    {
        if (clusters.Count <= 1) return clusters;

        var summaryEntries = clusters.Select((c, i) => new
        {
            cIndex = i,
            label = c.Label,
            suggestedArchetypeName = c.SuggestedArchetypeName,
            claimKindSignature = c.ClaimKindSignature,
            memberCount = c.MemberCount,
            exampleNames = ParseMemberEntries(c.MemberSubroutineIdsJson).Take(3).Select(m => m.SubroutineName).ToList(),
        });
        var entriesJson = JsonSerializer.Serialize(summaryEntries, DigestJsonOpts);

        var loaded = prompts.GetLatest("common", "dotnet8", "reconcile-pattern-clusters");
        if (loaded is null)
        {
            _logger.LogWarning(
                "No reconcile-pattern-clusters prompt registered; skipping reconciliation, keeping {Count} per-batch clusters unmerged.",
                clusters.Count);
            return clusters;
        }

        var rendered = prompts.Render(loaded, new Dictionary<string, string?>
        {
            ["corpusName"] = corpusName,
            ["clusterCount"] = clusters.Count.ToString(),
            ["entriesJson"] = entriesJson,
        });

        if (!string.Equals(provider.Info.Name, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            // Mock provider: no LLM judging available — same honesty posture
            // as StubMockResult, just leave every batch's clusters distinct.
            return clusters;
        }

        var llmResult = await CallAnthropicAsync(httpFactory, anthropicOpts, rendered.System, rendered.User, ct);
        var mergeGroups = ParseMergeGroups(llmResult.RawJson, clusters.Count);
        if (mergeGroups.Count == 0) return clusters;

        _runLogger.Log(runId,
            $"Reconciliation: {mergeGroups.Count} merge group(s) found across {clusters.Count} per-batch clusters.");

        var merged = new List<PatternCluster>();
        var consumed = new HashSet<int>();
        foreach (var group in mergeGroups)
        {
            var indices = group.ClusterIndices.Where(i => !consumed.Contains(i)).Distinct().ToList();
            if (indices.Count < 2) continue; // already consumed by an earlier group, or degenerate — no-op
            foreach (var i in indices) consumed.Add(i);

            var sourceClusters = indices.Select(i => clusters[i]).ToList();
            var mergedMembers = sourceClusters
                .SelectMany(c => ParseMemberEntries(c.MemberSubroutineIdsJson))
                .GroupBy(m => m.SubroutineId)
                .Select(g => g.First())
                .ToList();

            merged.Add(new PatternCluster
            {
                Id = Guid.NewGuid(),
                PatternAnalysisRunId = runId,
                CorpusId = sourceClusters[0].CorpusId,
                ClaimKindSignature = string.Join(",",
                    sourceClusters.Select(c => c.ClaimKindSignature).Where(s => !string.IsNullOrEmpty(s)).Distinct()),
                Label = TruncateTo(
                    !string.IsNullOrWhiteSpace(group.MergedLabel) ? group.MergedLabel : sourceClusters[0].Label, 256),
                SuggestedArchetypeName = TruncateTo(
                    !string.IsNullOrWhiteSpace(group.MergedSuggestedArchetypeName)
                        ? group.MergedSuggestedArchetypeName
                        : sourceClusters[0].SuggestedArchetypeName, 128),
                Rationale = !string.IsNullOrWhiteSpace(group.MergedRationale)
                    ? group.MergedRationale
                    : "Merged across batches during reconciliation: " +
                      string.Join(" | ", sourceClusters.Select(c => c.Rationale)),
                MemberSubroutineIdsJson = JsonSerializer.Serialize(mergedMembers.Select(m => new
                {
                    subroutineId = m.SubroutineId,
                    subroutineName = m.SubroutineName,
                    specId = m.SpecId,
                })),
                MemberCount = mergedMembers.Count,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var result = new List<PatternCluster>(clusters.Count - consumed.Count + merged.Count);
        for (var i = 0; i < clusters.Count; i++)
        {
            if (!consumed.Contains(i)) result.Add(clusters[i]);
        }
        result.AddRange(merged);
        return result;
    }

    private sealed record MergeGroup(
        List<int> ClusterIndices, string MergedLabel, string MergedSuggestedArchetypeName, string MergedRationale);

    private static List<MergeGroup> ParseMergeGroups(string rawJson, int clusterCount)
    {
        var groups = new List<MergeGroup>();
        var json = ExtractFirstJsonObject(rawJson);
        if (json is null) return groups;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mergeGroups", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return groups;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var indices = new List<int>();
                if (item.TryGetProperty("clusterIndices", out var idxArr) && idxArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in idxArr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var idx)
                            && idx >= 0 && idx < clusterCount)
                        {
                            indices.Add(idx);
                        }
                    }
                }
                if (indices.Count < 2) continue; // a "merge" of fewer than 2 clusters is a no-op
                groups.Add(new MergeGroup(
                    indices,
                    ReadString(item, "mergedLabel", ""),
                    ReadString(item, "mergedSuggestedArchetypeName", ""),
                    ReadString(item, "mergedRationale", "")));
            }
        }
        catch
        {
            // Malformed reconciliation output just means no merges apply;
            // the per-batch clusters already parsed successfully stand.
        }
        return groups;
    }

    private static List<PatternCluster> ParseClusters(
        string rawJson, Guid runId, Guid corpusId,
        Dictionary<Guid, string> nameById,
        Dictionary<Guid, Guid> specIdBySubroutine,
        Dictionary<Guid, string> signatureBySubroutine,
        IReadOnlyList<Guid> subroutineIdByIndex)
    {
        var clusters = new List<PatternCluster>();
        var json = ExtractFirstJsonObject(rawJson);
        if (json is null)
        {
            clusters.Add(new PatternCluster
            {
                Id = Guid.NewGuid(),
                PatternAnalysisRunId = runId,
                CorpusId = corpusId,
                Label = "Clustering output could not be parsed",
                Rationale = "The LLM emitted text that did not parse as JSON. First 400 chars: " + Truncate(rawJson, 400),
                MemberSubroutineIdsJson = "[]",
                MemberCount = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            return clusters;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("clusters", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return clusters;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var memberIds = new List<Guid>();
                // Preferred shape: "members" — integer indexes into the input
                // entry list (see BuildEntriesJsonAtTier's `n`).
                if (item.TryGetProperty("members", out var idxArr) && idxArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in idxArr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Number
                            && el.TryGetInt32(out var idx)
                            && idx >= 0 && idx < subroutineIdByIndex.Count)
                        {
                            memberIds.Add(subroutineIdByIndex[idx]);
                        }
                    }
                }
                // Legacy shape: full GUID strings (the mock stub still emits this).
                if (memberIds.Count == 0
                    && item.TryGetProperty("memberSubroutineIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var idEl in ids.EnumerateArray())
                    {
                        if (idEl.ValueKind == JsonValueKind.String
                            && Guid.TryParse(idEl.GetString(), out var gid))
                        {
                            memberIds.Add(gid);
                        }
                    }
                }
                if (memberIds.Count == 0) continue;

                var membersJson = JsonSerializer.Serialize(memberIds.Select(id => new
                {
                    subroutineId = id,
                    subroutineName = nameById.TryGetValue(id, out var n) ? n : id.ToString(),
                    specId = specIdBySubroutine.TryGetValue(id, out var sid) ? (Guid?)sid : null,
                }));

                clusters.Add(new PatternCluster
                {
                    Id = Guid.NewGuid(),
                    PatternAnalysisRunId = runId,
                    CorpusId = corpusId,
                    ClaimKindSignature = signatureBySubroutine.TryGetValue(memberIds[0], out var sig0) ? sig0 : "",
                    Label = TruncateTo(ReadString(item, "label", "(unlabeled cluster)"), 256),
                    SuggestedArchetypeName = TruncateTo(ReadString(item, "suggestedArchetypeName", ""), 128),
                    Rationale = ReadString(item, "rationale", ""),
                    MemberSubroutineIdsJson = membersJson,
                    MemberCount = memberIds.Count,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        catch
        {
            // Already covered by the surrounding logic; leave whatever
            // clusters parsed successfully before the failure.
        }
        return clusters;
    }

    private static IEnumerable<Guid> ParseMemberIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("subroutineId", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idEl.GetString(), out var gid))
                {
                    yield return gid;
                }
            }
        }
        finally { }
    }

    private static string? ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static string ReadString(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback)
            : fallback;

    private static string TruncateTo(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private async Task UpdateAsync(Guid runId, string state, string summary, IDictionary<string, object?>? metrics, bool completed = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PatternAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (row is null) return;
        row.State = state;
        row.Summary = summary.Length > 1024 ? summary[..1024] : summary;
        if (metrics is not null) row.MetricsJson = JsonSerializer.Serialize(metrics);
        if (completed) row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task FailAsync(Guid runId, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PatternAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (row is null) return;
        row.State = "FAILED";
        row.ErrorSummary = message.Length > 4000 ? message[..4000] : message;
        row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private sealed record ClusterLlmResult(string RawJson, int InputTokens, int OutputTokens);
}
