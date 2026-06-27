using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.a — production routine-summary pipeline.
///
/// Differences vs. the 11.0 vertical slice (<see cref="DocsExtractionService"/>):
///
///  * **Model tiering.** RoutineTierClassifier picks utility / mid / headline
///    per routine; the pipeline dispatches to Haiku (batched) / Sonnet
///    (single-shot) / Opus (single-shot) accordingly.
///  * **Prompt caching.** The system message uses Anthropic's cache_control:
///    ephemeral block so the second call within 5 minutes pays the 90%
///    input-token discount on the shared rules + worked example.
///  * **Batched utility tier.** Haiku calls aggregate <see cref="DocsOptions.BatchSize"/>
///    routines per request; the LLM returns a JSON ARRAY of summary
///    objects in the same order.
///  * **Bounded concurrency.** A <see cref="SemaphoreSlim"/> caps parallel
///    Anthropic requests at <see cref="DocsOptions.MaxConcurrency"/> so we
///    stay comfortably below the rate ceiling.
///  * **Idempotency.** Routines with an existing routine-summary DocSection
///    for the same SourceVersion are skipped (unless force=true).
///  * **Background mode.** The endpoint creates a DocGenerationRun and
///    returns 202 + run ID; the pipeline progresses on a Task.Run scoped
///    to a fresh DI scope (the request scope dies on response send).
///  * **Incremental metrics.** MetricsJson is updated on every batch so
///    the UI can poll progress.
/// </summary>
public sealed class RoutineSummaryPipeline
{
    private const string SingleShotPromptId = "fortran-doc-summary";
    private const string SingleShotPromptVersion = "v1.0";
    private const string BatchPromptId = "fortran-doc-summary-batch";
    private const string BatchPromptVersion = "v1.0";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly string _singleShotPrompt;
    private readonly string _batchPrompt;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private readonly ILogger<RoutineSummaryPipeline> _logger;

    public RoutineSummaryPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IConfiguration cfg,
        IWebHostEnvironment env,
        ILogger<RoutineSummaryPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _logger = logger;
        _apiKey = cfg.GetValue<string>("Llm:Anthropic:ApiKey") ?? "";
        _baseUrl = cfg.GetValue<string>("Llm:Anthropic:BaseUrl") ?? "https://api.anthropic.com";
        _apiVersion = cfg.GetValue<string>("Llm:Anthropic:ApiVersion") ?? "2023-06-01";

        _singleShotPrompt = StripFrontmatter(File.ReadAllText(
            ResolvePromptPath(env.ContentRootPath, "doc-summary.v1.md")));
        _batchPrompt = StripFrontmatter(File.ReadAllText(
            ResolvePromptPath(env.ContentRootPath, "doc-summary-batch.v1.md")));
    }

    public sealed record GenerateOptions(int? Take, bool Force);

    /// <summary>Create a DocGenerationRun row and background the work. Returns the run ID.</summary>
    public async Task<Guid> StartAsync(Guid corpusId, GenerateOptions opts, CancellationToken ct)
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

            var run = new DocGenerationRun
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                StagesRequested = "routine-summary",
                State = "QUEUED",
                Summary = "Queued",
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.DocGenerationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            runId = run.Id;
        }

        // Fire-and-forget. Pattern matches DevEndpoints' background-reset flow:
        // the request scope dies the moment the endpoint responds, so the
        // worker must create its own scope and use its own CancellationToken.
        _ = Task.Run(() => RunAsync(runId, corpusId, sourceVersionId, opts));
        return runId;
    }

    private async Task RunAsync(Guid runId, Guid corpusId, Guid sourceVersionId, GenerateOptions opts)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
            var classifier = scope.ServiceProvider.GetRequiredService<RoutineTierClassifier>();
            var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var ct = CancellationToken.None;

            await MarkAsync(db, runId, "RUNNING", "Loading corpus", null, ct);

            var tiers = await classifier.ClassifyForCorpusAsync(corpusId, sourceVersionId, ct);

            // Skip routines whose routine-summary DocSection already exists
            // for the current SourceVersion (idempotency). Force = true wipes
            // and re-extracts.
            var existing = await db.DocSections
                .AsNoTracking()
                .Where(s => s.CorpusId == corpusId
                         && s.SourceVersionId == sourceVersionId
                         && s.SectionKind == "routine-summary"
                         && s.SubroutineId != null)
                .Select(s => s.SubroutineId!.Value)
                .ToListAsync(ct);

            var subs = await db.Subroutines
                .Include(s => s.SourceFile)
                .AsNoTracking()
                .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
                .OrderBy(s => s.SourceFile!.RelativePath)
                .ThenBy(s => s.LineStart)
                .ToListAsync(ct);

            var existingSet = existing.ToHashSet();
            var todo = subs.Where(s => opts.Force || !existingSet.Contains(s.Id)).ToList();
            if (opts.Take is int take)
                todo = todo.Take(Math.Max(1, take)).ToList();

            if (opts.Force && todo.Count > 0)
            {
                var todoIds = todo.Select(t => t.Id).ToHashSet();
                await db.DocSections
                    .Where(s => s.SubroutineId != null
                             && s.SectionKind == "routine-summary"
                             && todoIds.Contains(s.SubroutineId!.Value))
                    .ExecuteDeleteAsync(ct);
            }

            var summary = $"{todo.Count} todo · {existing.Count} skipped";
            await MarkAsync(db, runId, "RUNNING", summary, null, ct);

            if (todo.Count == 0)
            {
                await MarkAsync(db, runId, "SUCCEEDED", "Nothing to do",
                    new MetricsAccumulator { Skipped = existing.Count }, ct, completed: true);
                return;
            }

            var metrics = new MetricsAccumulator { Skipped = existing.Count };
            var utility = new List<Subroutine>();
            var mid = new List<Subroutine>();
            var headline = new List<Subroutine>();
            foreach (var sub in todo)
            {
                var tier = tiers.TryGetValue(sub.Id, out var t) ? t.Tier : "utility";
                switch (tier)
                {
                    case "headline": headline.Add(sub); break;
                    case "mid":       mid.Add(sub); break;
                    default:           utility.Add(sub); break;
                }
            }

            var sem = new SemaphoreSlim(Math.Max(1, _docsOpts.MaxConcurrency));
            var pendingSingle = new List<Task>();

            // Single-shot tasks (headline / mid). Each runs through its own scope.
            foreach (var sub in headline)
                pendingSingle.Add(RunSingleAsync(sub, "headline", _docsOpts.OpusModel, sem, runId, corpusId, sourceVersionId, metrics, httpFactory, blob, ct));
            foreach (var sub in mid)
                pendingSingle.Add(RunSingleAsync(sub, "mid", _docsOpts.SonnetModel, sem, runId, corpusId, sourceVersionId, metrics, httpFactory, blob, ct));

            // Utility batches. Group by source file so the batch shares context.
            var batchSize = Math.Max(1, _docsOpts.BatchSize);
            var batches = utility
                .GroupBy(s => s.SourceFileId)
                .SelectMany(g => Partition(g.ToList(), batchSize))
                .ToList();
            foreach (var batch in batches)
                pendingSingle.Add(RunBatchAsync(batch, sem, runId, corpusId, sourceVersionId, metrics, httpFactory, blob, ct));

            await Task.WhenAll(pendingSingle);

            // Final metrics flush.
            using (var finalScope = _scopeFactory.CreateScope())
            {
                var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var state = metrics.Failed == 0
                    ? "SUCCEEDED"
                    : (metrics.Succeeded > 0 ? "PARTIAL" : "FAILED");
                var summaryText = $"{metrics.Succeeded}/{todo.Count} succeeded · {metrics.Failed} failed · {metrics.Skipped} skipped";
                await MarkAsync(finalDb, runId, state, summaryText, metrics, ct, completed: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Doc generation run {RunId} crashed", runId);
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.DocGenerationRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (row is null) return;
            row.State = "FAILED";
            row.ErrorSummary = ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message;
            row.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private async Task RunSingleAsync(
        Subroutine sub, string tier, string model, SemaphoreSlim sem,
        Guid runId, Guid corpusId, Guid sourceVersionId,
        MetricsAccumulator metrics, IHttpClientFactory httpFactory, IBlobClient blob,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var source = await blob.GetTextAsync(sub.SourceFile!.BlobUri, ct);
            var routineLines = ExtractRoutineLines(source, sub.LineStart, sub.LineEnd);
            var user = BuildSingleUserMessage(sub.Name, TryGetEnclosingModule(sub), tier, routineLines);

            var (payload, usage) = await CallAnthropicAsync(
                httpFactory, model, _singleShotPrompt, user, expectArray: false, ct);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var section = new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                SectionKind = "routine-summary",
                Scope = "subroutine",
                SubroutineId = sub.Id,
                State = "DRAFT",
                PayloadJson = JsonDocument.Parse(payload),
                RenderedMarkdown = RenderMarkdown(payload, sub.Name),
                GenerationRunId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.DocSections.Add(section);
            await db.SaveChangesAsync(ct);

            metrics.Record(tier, 1, usage.input, usage.output, usage.cacheRead, usage.cacheCreate);
            await UpdateMetricsAsync(db, runId, metrics, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Single-shot doc summary failed for {Routine}", sub.Name);
            metrics.Failed++;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task RunBatchAsync(
        IReadOnlyList<Subroutine> batch, SemaphoreSlim sem,
        Guid runId, Guid corpusId, Guid sourceVersionId,
        MetricsAccumulator metrics, IHttpClientFactory httpFactory, IBlobClient blob,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            // All routines in the batch share a SourceFile (we grouped that way),
            // so one blob read covers them all.
            var fileSource = await blob.GetTextAsync(batch[0].SourceFile!.BlobUri, ct);
            var userBuilder = new StringBuilder();
            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) userBuilder.Append("\n---\n\n");
                var sub = batch[i];
                userBuilder.Append("ROUTINE ").Append(i + 1).Append('\n');
                userBuilder.Append("routine_name: ").Append(sub.Name).Append('\n');
                userBuilder.Append("enclosing_module: ").Append(TryGetEnclosingModule(sub) ?? "null").Append('\n');
                userBuilder.Append("source:\n");
                userBuilder.Append(ExtractRoutineLines(fileSource, sub.LineStart, sub.LineEnd));
            }

            var (payload, usage) = await CallAnthropicAsync(
                httpFactory, _docsOpts.HaikuModel, _batchPrompt, userBuilder.ToString(),
                expectArray: true, ct);

            using var arrayDoc = JsonDocument.Parse(payload);
            var arr = arrayDoc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != batch.Count)
                throw new InvalidOperationException(
                    $"Batch response shape mismatch: expected array of {batch.Count}, got {arr.ValueKind}/{(arr.ValueKind == JsonValueKind.Array ? arr.GetArrayLength() : 0)}");

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < batch.Count; i++)
            {
                var sub = batch[i];
                var itemJson = arr[i].GetRawText();
                var section = new DocSection
                {
                    Id = Guid.NewGuid(),
                    CorpusId = corpusId,
                    SourceVersionId = sourceVersionId,
                    SectionKind = "routine-summary",
                    Scope = "subroutine",
                    SubroutineId = sub.Id,
                    State = "DRAFT",
                    PayloadJson = JsonDocument.Parse(itemJson),
                    RenderedMarkdown = RenderMarkdown(itemJson, sub.Name),
                    GenerationRunId = runId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.DocSections.Add(section);
            }
            await db.SaveChangesAsync(ct);

            metrics.Record("utility", batch.Count, usage.input, usage.output, usage.cacheRead, usage.cacheCreate);
            await UpdateMetricsAsync(db, runId, metrics, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batched doc summary failed for {Count} routines starting with {First}",
                batch.Count, batch[0].Name);
            metrics.Failed += batch.Count;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<(string payload, TokenUsage usage)> CallAnthropicAsync(
        IHttpClientFactory httpFactory, string model, string systemPrompt, string userMessage,
        bool expectArray, CancellationToken ct)
    {
        // Cache the system prompt — for 11.0.a it's the load-bearing
        // discount surface. cache_control:ephemeral lasts 5 minutes; the
        // shared rules + worked example pay full price on the first call
        // and 10% on every subsequent call within the window.
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
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
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userMessage },
            },
        };

        var http = httpFactory.CreateClient("docs-summary");
        http.Timeout = TimeSpan.FromMinutes(3);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", _apiVersion);

        using var resp = await http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic {(int)resp.StatusCode}: {Truncate(bodyText, 400)}");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        string? text = null;
        foreach (var block in root.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                text = block.GetProperty("text").GetString();
        if (text is null) throw new InvalidOperationException("No text block in Anthropic response.");

        var payload = expectArray ? TrimToJsonArray(text) : TrimToJsonObject(text);
        using (var _ = JsonDocument.Parse(payload)) { /* parse-validate */ }

        var usage = root.GetProperty("usage");
        int cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
        int cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
        return (payload, new TokenUsage(
            usage.GetProperty("input_tokens").GetInt32(),
            usage.GetProperty("output_tokens").GetInt32(),
            cacheRead, cacheCreate));
    }

    private static async Task MarkAsync(
        AppDbContext db, Guid runId, string state, string summary,
        MetricsAccumulator? metrics, CancellationToken ct, bool completed = false)
    {
        var row = await db.DocGenerationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (row is null) return;
        row.State = state;
        row.Summary = summary.Length > 1024 ? summary[..1024] : summary;
        if (metrics is not null) row.MetricsJson = metrics.ToJson();
        if (completed) row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task UpdateMetricsAsync(
        AppDbContext db, Guid runId, MetricsAccumulator metrics, CancellationToken ct)
    {
        var row = await db.DocGenerationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (row is null) return;
        row.MetricsJson = metrics.ToJson();
        row.Summary = $"In flight · {metrics.Succeeded} ok · {metrics.Failed} failed";
        await db.SaveChangesAsync(ct);
    }

    private static string BuildSingleUserMessage(string routineName, string? enclosing, string tier, string sourceLines) =>
        $"routine_name: {routineName}\nenclosing_module: {enclosing ?? "null"}\ntier: {tier}\nsource:\n{sourceLines}";

    private static IEnumerable<IReadOnlyList<T>> Partition<T>(IList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
            yield return items.Skip(i).Take(size).ToList();
    }

    private static string ExtractRoutineLines(string fullSource, int lineStart, int lineEnd)
    {
        if (lineStart <= 0 || lineEnd <= 0 || lineEnd < lineStart) return fullSource;
        var allLines = fullSource.Replace("\r\n", "\n").Split('\n');
        var lo = Math.Max(0, lineStart - 1);
        var hi = Math.Min(allLines.Length, lineEnd);
        var sb = new StringBuilder();
        for (var i = lo; i < hi; i++)
            sb.Append(i + 1).Append(": ").Append(allLines[i]).Append('\n');
        return sb.ToString();
    }

    private static string? TryGetEnclosingModule(Subroutine sub) =>
        sub.SourceFile?.RelativePath is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    private static string TrimToJsonObject(string text)
    {
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close <= open) throw new InvalidOperationException("Not a JSON object: " + Truncate(text, 200));
        return text[open..(close + 1)];
    }

    private static string TrimToJsonArray(string text)
    {
        var open = text.IndexOf('[');
        var close = text.LastIndexOf(']');
        if (open < 0 || close <= open) throw new InvalidOperationException("Not a JSON array: " + Truncate(text, 200));
        return text[open..(close + 1)];
    }

    private static string RenderMarkdown(string payloadJson, string routineName)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "";
        var sb = new StringBuilder();
        sb.Append("### ").Append(routineName).Append("\n\n").Append(summary).Append("\n\n");
        AppendList(sb, root, "inputs", "**Inputs**");
        AppendList(sb, root, "outputs", "**Outputs**");
        AppendList(sb, root, "sideEffects", "**Side effects**");
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, JsonElement root, string prop, string heading)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return;
        sb.Append(heading).Append('\n');
        foreach (var item in arr.EnumerateArray())
            sb.Append("- ").Append(item.GetString()).Append('\n');
        sb.Append('\n');
    }

    private static string StripFrontmatter(string md)
    {
        if (!md.StartsWith("---")) return md;
        var endIdx = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return md;
        var after = endIdx + 4;
        while (after < md.Length && (md[after] == '\n' || md[after] == '\r')) after++;
        return md[after..];
    }

    private static string ResolvePromptPath(string contentRoot, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Llm", "Prompts", "fortran-f77", fileName),
            Path.Combine(contentRoot, "..", "..", "Llm", "Prompts", "fortran-f77", fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        throw new FileNotFoundException($"Prompt {fileName} not found. Searched: {string.Join(", ", candidates)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private record TokenUsage(int input, int output, int cacheRead, int cacheCreate);

    private sealed class MetricsAccumulator
    {
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        private readonly Dictionary<string, TierMetrics> _byTier = new()
        {
            ["headline"] = new TierMetrics(),
            ["mid"] = new TierMetrics(),
            ["utility"] = new TierMetrics(),
        };
        private readonly object _lock = new();

        public void Record(string tier, int sections, int inputTokens, int outputTokens, int cacheRead, int cacheCreate)
        {
            lock (_lock)
            {
                Succeeded += sections;
                if (!_byTier.TryGetValue(tier, out var m))
                {
                    m = new TierMetrics();
                    _byTier[tier] = m;
                }
                m.Sections += sections;
                m.InputTokens += inputTokens;
                m.OutputTokens += outputTokens;
                m.CacheReadTokens += cacheRead;
                m.CacheCreationTokens += cacheCreate;
                m.Calls += 1;
            }
        }

        public string ToJson()
        {
            lock (_lock)
            {
                var obj = new Dictionary<string, object?>
                {
                    ["succeeded"] = Succeeded,
                    ["failed"] = Failed,
                    ["skipped"] = Skipped,
                    ["byTier"] = _byTier.ToDictionary(kv => kv.Key, kv => (object)new
                    {
                        sections = kv.Value.Sections,
                        calls = kv.Value.Calls,
                        inputTokens = kv.Value.InputTokens,
                        outputTokens = kv.Value.OutputTokens,
                        cacheReadTokens = kv.Value.CacheReadTokens,
                        cacheCreationTokens = kv.Value.CacheCreationTokens,
                    }),
                };
                return JsonSerializer.Serialize(obj);
            }
        }

        private sealed class TierMetrics
        {
            public int Sections { get; set; }
            public int Calls { get; set; }
            public int InputTokens { get; set; }
            public int OutputTokens { get; set; }
            public int CacheReadTokens { get; set; }
            public int CacheCreationTokens { get; set; }
        }
    }
}
