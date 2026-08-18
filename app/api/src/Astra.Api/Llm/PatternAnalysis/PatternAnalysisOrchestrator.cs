using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
///     (<see cref="Validation.ClaimKindBucketer"/>), then send the whole
///     corpus to the LLM in ONE call (mirrors <c>HarmonisationPipeline</c>)
///     asking it to group routines that share one real behavioural
///     pattern, producing <see cref="PatternCluster"/> rows with a
///     suggested archetype name per cluster.
///
/// One call (not one per bucket) so the model can split a bucket whose
/// members only superficially share claim kinds, or merge across buckets
/// when the same real idiom happens to produce slightly different claim
/// sets — decisions that require seeing the whole corpus at once.
/// </summary>
public sealed class PatternAnalysisOrchestrator
{
    private const int MaxExtractConcurrency = 4;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatternAnalysisOrchestrator> _logger;

    public PatternAnalysisOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<PatternAnalysisOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
                : (extractResult.Failed > 0 ? "PARTIAL" : "SUCCEEDED");
            var summary =
                $"{extractResult.Succeeded} extracted ({extractResult.Failed} failed, " +
                $"{extractResult.Skipped} already-extracted) → {clusterResult.ClusterCount} cluster(s) " +
                $"across {clusterResult.SubroutineCount} routine(s).";
            await UpdateAsync(runId, state, summary,
                new Dictionary<string, object?> { ["extract"] = extractResult, ["cluster"] = clusterResult },
                completed: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pattern analysis run {RunId} crashed", runId);
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

        var sem = new SemaphoreSlim(MaxExtractConcurrency);
        var results = await Task.WhenAll(todo.Select(sub => ExtractOneAsync(sub, sem, ct)));
        var succeeded = results.Count(r => r);
        return new ExtractStageResult(succeeded, results.Length - succeeded, skipped);
    }

    private async Task<bool> ExtractOneAsync(Subroutine sub, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<ExtractionPipeline>();
            var ok = false;
            await foreach (var evt in pipeline.RunAsync(sub.Id, ct))
            {
                if (evt.Type == "done")
                {
                    ok = true;
                }
                else if (evt.Type == "error")
                {
                    _logger.LogWarning(
                        "Bulk extraction failed for {Sub}: {Err}", sub.Name, JsonSerializer.Serialize(evt.Data));
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk extraction threw for {Sub}", sub.Name);
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    // ── Stage 2: claim-kind bucketing + LLM-judged clustering ───────────

    private sealed record ClusterStageResult(int SubroutineCount, int ClusterCount, int InputTokens, int OutputTokens);

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
            return new ClusterStageResult(0, 0, 0, 0);

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
        var (entriesJson, digestTier) = BuildEntriesJson(digests);
        _logger.LogInformation(
            "Pattern clustering digest: corpus={CorpusId} routines={Count} tier={Tier} chars={Chars}",
            corpusId, digests.Count, digestTier, entriesJson.Length);

        var loaded = prompts.GetLatest("common", "dotnet8", "cluster-patterns")
            ?? throw new InvalidOperationException(
                "No cluster-patterns prompt registered (common/dotnet8/cluster-patterns).");

        var rendered = prompts.Render(loaded, new Dictionary<string, string?>
        {
            ["corpusName"] = corpus.Name,
            ["sourceVersionId"] = sourceVersionId.ToString(),
            ["subroutineCount"] = specs.Count.ToString(),
            ["entriesJson"] = entriesJson,
        });

        ClusterLlmResult llmResult;
        if (string.Equals(provider.Info.Name, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            llmResult = await CallAnthropicAsync(httpFactory, anthropicOpts, rendered.System, rendered.User, ct);
        }
        else
        {
            llmResult = StubMockResult(specs, signatureBySubroutine);
        }

        var specIdBySubroutine = specs.ToDictionary(s => s.SubroutineId, s => s.Id);
        var clusters = ParseClusters(llmResult.RawJson, runId, corpusId, nameById, specIdBySubroutine, signatureBySubroutine);

        // Safety net: any subroutine the LLM didn't place gets its own
        // singleton cluster rather than silently vanishing from the report.
        var placed = clusters
            .SelectMany(c => ParseMemberIds(c.MemberSubroutineIdsJson))
            .ToHashSet();
        foreach (var spec in specs)
        {
            if (placed.Contains(spec.SubroutineId)) continue;
            var name = nameById.TryGetValue(spec.SubroutineId, out var n) ? n : spec.SubroutineId.ToString();
            clusters.Add(new PatternCluster
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

        await db.PatternClusters.AddRangeAsync(clusters, ct);
        await db.SaveChangesAsync(ct);

        return new ClusterStageResult(specs.Count, clusters.Count, llmResult.InputTokens, llmResult.OutputTokens);
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

    private static (string Json, string Tier) BuildEntriesJson(IReadOnlyList<RoutineDigest> digests)
    {
        var tiers = new (string Name, int ClaimsPerKind, int ClaimChars, int PurposeChars)[]
        {
            ("excerpts", 4, 200, 300),
            ("short-excerpts", 2, 110, 160),
            ("counts-only", 0, 0, 110),
        };

        var json = "";
        var tierName = "";
        foreach (var tier in tiers)
        {
            tierName = tier.Name;
            var entries = digests.Select(d => new
            {
                subroutineId = d.SubroutineId,
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
            });
            json = JsonSerializer.Serialize(entries, DigestJsonOpts);
            if (json.Length <= MaxEntriesJsonChars) return (json, tierName);
        }

        // counts-only is ~100 bytes per routine; if a pathological corpus
        // still exceeds the budget, send it anyway rather than refusing.
        return (json, tierName);
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
        var http = httpFactory.CreateClient("anthropic-cluster-patterns");
        http.Timeout = TimeSpan.FromMinutes(10);

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

    private static List<PatternCluster> ParseClusters(
        string rawJson, Guid runId, Guid corpusId,
        Dictionary<Guid, string> nameById,
        Dictionary<Guid, Guid> specIdBySubroutine,
        Dictionary<Guid, string> signatureBySubroutine)
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
                if (item.TryGetProperty("memberSubroutineIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
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
