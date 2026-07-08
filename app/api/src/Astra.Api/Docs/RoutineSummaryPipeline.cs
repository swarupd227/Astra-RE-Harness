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
/// Phase 11.0.f quality upgrade: Haiku batch tier removed. All routines run
/// single-shot with a 4096-token output budget.  Two tiers remain:
///   * **headline** (top <see cref="DocsOptions.HeadlinePercentile"/> % by
///     composite importance score) → Opus.
///   * **standard** (remainder) → Sonnet.
///
/// Other features retained from 11.0.a:
///  * **Prompt caching.** System message cached with cache_control:ephemeral;
///    first call pays full price, subsequent calls within 5 min pay 10%.
///  * **Caller context.** The user message now includes the names of routines
///    that call this one, giving the model richer context to write accurate
///    domain-language summaries.
///  * **Bounded concurrency.** SemaphoreSlim caps parallel requests at
///    <see cref="DocsOptions.MaxConcurrency"/>.
///  * **Idempotency.** Skips existing sections for same SourceVersion unless
///    force=true.
///  * **Background mode.** Returns 202 + run ID; worker scoped separately.
///  * **Incremental metrics.** MetricsJson updated per call for UI polling.
/// </summary>
public sealed class RoutineSummaryPipeline
{
    private const string PromptId = "fortran-doc-summary";
    private const string PromptVersion = "v2.0";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly string _prompt;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private readonly DocRunLogger _runLogger;
    private readonly ILogger<RoutineSummaryPipeline> _logger;

    public RoutineSummaryPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IConfiguration cfg,
        IWebHostEnvironment env,
        DocRunLogger runLogger,
        ILogger<RoutineSummaryPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _runLogger = runLogger;
        _logger = logger;
        _apiKey = cfg.GetValue<string>("Llm:Anthropic:ApiKey") ?? "";
        _baseUrl = cfg.GetValue<string>("Llm:Anthropic:BaseUrl") ?? "https://api.anthropic.com";
        _apiVersion = cfg.GetValue<string>("Llm:Anthropic:ApiVersion") ?? "2023-06-01";

        _prompt = StripFrontmatter(File.ReadAllText(
            ResolvePromptPath(env.ContentRootPath, "doc-summary.v1.md")));
    }

    public sealed record GenerateOptions(int? Take, bool Force);

    public sealed record StageResult(int Succeeded, int Failed, int Skipped, string MetricsJson);

    /// <summary>
    /// Phase 11.0.a single-stage entry point: create a DocGenerationRun
    /// row and background the routine-summary work. Kept for backward
    /// compat with callers that want exactly one stage; the 11.0.b
    /// multi-stage orchestrator takes a different entry point and uses
    /// <see cref="RunRoutineSummaryStageAsync"/> directly.
    /// </summary>
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
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await RunRoutineSummaryStageAsync(runId, corpusId, sourceVersionId, opts, CancellationToken.None);
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var state = result.Failed == 0
                    ? "SUCCEEDED"
                    : (result.Succeeded > 0 ? "PARTIAL" : "FAILED");
                var summary = $"{result.Succeeded}/{result.Succeeded + result.Failed} succeeded · {result.Failed} failed · {result.Skipped} skipped";
                var row = await db.DocGenerationRuns.FirstOrDefaultAsync(r => r.Id == runId);
                if (row is null) return;
                row.State = state;
                row.Summary = summary.Length > 1024 ? summary[..1024] : summary;
                row.MetricsJson = result.MetricsJson;
                row.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
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
        });
        return runId;
    }

    /// <summary>
    /// Run the routine-summary stage against an EXISTING DocGenerationRun row.
    /// Does not mark the run completed — the orchestrator chains stages and
    /// only marks completion after the last stage finishes.
    /// </summary>
    public async Task<StageResult> RunRoutineSummaryStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, GenerateOptions opts, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
        var classifier = scope.ServiceProvider.GetRequiredService<RoutineTierClassifier>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

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
                var emptyMetrics = new MetricsAccumulator { Skipped = existing.Count };
                return new StageResult(0, 0, existing.Count, emptyMetrics.ToJson());
            }

            var metrics = new MetricsAccumulator { Skipped = existing.Count };
            var sem = new SemaphoreSlim(Math.Max(1, _docsOpts.MaxConcurrency));

            // All routines run single-shot. Headline tier → Opus; standard → Sonnet.
            var tasks = todo.Select(sub =>
            {
                var assignment = tiers.TryGetValue(sub.Id, out var t) ? t : null;
                var tier = assignment?.Tier ?? "standard";
                var model = tier == "headline" ? _docsOpts.OpusModel : _docsOpts.SonnetModel;
                var callerNames = assignment?.CallerNames ?? Array.Empty<string>();
                return RunSingleAsync(sub, tier, model, callerNames, sem, runId, corpusId, sourceVersionId, metrics, httpFactory, blob, ct);
            }).ToList();

            await Task.WhenAll(tasks);

            // Final per-stage metrics flush — but DON'T mark completed. The
            // single-stage StartAsync wrapper and the 11.0.b orchestrator
            // each handle their own completion marking.
            using (var finalScope = _scopeFactory.CreateScope())
            {
                var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await UpdateMetricsAsync(finalDb, runId, metrics, ct);
            }
        return new StageResult(metrics.Succeeded, metrics.Failed, metrics.Skipped, metrics.ToJson());
    }

    private async Task RunSingleAsync(
        Subroutine sub, string tier, string model, IReadOnlyList<string> callerNames,
        SemaphoreSlim sem, Guid runId, Guid corpusId, Guid sourceVersionId,
        MetricsAccumulator metrics, IHttpClientFactory httpFactory, IBlobClient blob,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var source = await blob.GetTextAsync(sub.SourceFile!.BlobUri, ct);
            var routineLines = ExtractRoutineLines(source, sub.LineStart, sub.LineEnd);
            var user = BuildUserMessage(sub.Name, TryGetEnclosingModule(sub), tier, callerNames, routineLines);

            var (payload, usage) = await CallAnthropicAsync(
                httpFactory, model, _prompt, user, ct);

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
            _logger.LogWarning(ex, "Doc summary failed for {Routine}", sub.Name);
            _runLogger.Log(runId, $"  ✗ '{sub.Name}': {FirstLine(ex.Message)}");
            metrics.Failed++;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<(string payload, TokenUsage usage)> CallAnthropicAsync(
        IHttpClientFactory httpFactory, string model, string systemPrompt, string userMessage,
        CancellationToken ct)
    {
        // Prompt caching: system prompt marked cache_control:ephemeral.
        // First call in any 5-minute window pays full input-token cost;
        // all subsequent calls pay 10% (the 90% cache discount). With
        // concurrency=6 and ~150 routines, most calls hit the cache.
        // Structured output via tool use: the model answers through a tool whose
        // input_schema we define, so the Anthropic API assembles the JSON. An
        // unescaped quote or control character in a string value (which broke the
        // old text-block parse on content-heavy routines) is now impossible — the
        // tool_use block's `input` is always valid JSON.
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
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "emit_routine_summary",
                    ["description"] = "Emit the routine's transition-documentation summary as structured fields.",
                    ["input_schema"] = RoutineSummaryToolSchema,
                },
            },
            ["tool_choice"] = new Dictionary<string, object?>
            {
                ["type"] = "tool",
                ["name"] = "emit_routine_summary",
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
        req.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

        using var resp = await http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic {(int)resp.StatusCode}: {Truncate(bodyText, 400)}");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        string? payload = null;
        foreach (var block in root.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("input", out var input))
            {
                payload = input.GetRawText();
                break;
            }
        if (payload is null)
            throw new InvalidOperationException(
                "No tool_use block in Anthropic response: " + Truncate(bodyText, 300));

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

    private static string BuildUserMessage(
        string routineName, string? enclosing, string tier,
        IReadOnlyList<string> callerNames, string sourceLines)
    {
        var sb = new StringBuilder();
        sb.Append("routine_name: ").Append(routineName).Append('\n');
        sb.Append("enclosing_module: ").Append(enclosing ?? "null").Append('\n');
        sb.Append("tier: ").Append(tier).Append('\n');
        if (callerNames.Count > 0)
            sb.Append("callers: ").Append(string.Join(", ", callerNames.Take(10))).Append('\n');
        sb.Append("source:\n").Append(sourceLines);
        return sb.ToString();
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

    // ── Tool input schema (structured-output contract) ──────────────────
    // Mirrors the doc-summary prompt's JSON shape. Only `summary` is required
    // so a pure routine with no side effects still validates; RenderMarkdown
    // reads every field defensively.

    private static Dictionary<string, object?> StringArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
    };

    private static readonly object RoutineSummaryToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["summary"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "3–6 sentences in domain language: what the routine does, why a caller uses it, and the key constraints.",
            },
            ["inputs"] = StringArray("Named inputs in domain terms."),
            ["outputs"] = StringArray("Named outputs in domain terms."),
            ["sideEffects"] = StringArray("Mutations beyond the return value (files, COMMON blocks, I/O, errors)."),
            ["preconditions"] = StringArray("Caller-must-satisfy contracts."),
            ["edgeCases"] = StringArray("Boundary conditions and numerical traps."),
            ["tier"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = new[] { "headline", "standard" },
            },
            ["citations"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["lines"] = new Dictionary<string, object?> { ["type"] = "string" },
                    },
                },
            },
        },
        ["required"] = new[] { "summary" },
    };

    private static string RenderMarkdown(string payloadJson, string routineName)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "";
        var sb = new StringBuilder();
        sb.Append("### ").Append(routineName).Append("\n\n").Append(summary).Append("\n\n");
        AppendTable(sb, root, "inputs",        "#### Inputs",        "Description");
        AppendTable(sb, root, "outputs",       "#### Outputs",       "Description");
        AppendCallouts(sb, root, "sideEffects",   "#### Side Effects");
        AppendTable(sb, root, "preconditions", "#### Preconditions", "Requirement");
        AppendCallouts(sb, root, "edgeCases",     "#### Edge Cases");
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, JsonElement root, string prop, string heading, string col)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return;
        sb.Append(heading).Append("\n\n");
        sb.Append("| # | ").Append(col).Append(" |\n");
        sb.Append("|--:|--------|\n");
        int i = 1;
        foreach (var item in arr.EnumerateArray())
            sb.Append("| ").Append(i++).Append(" | ").Append(EscapeTableCell(item.GetString() ?? "")).Append(" |\n");
        sb.Append('\n');
    }

    private static void AppendCallouts(StringBuilder sb, JsonElement root, string prop, string heading)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return;
        sb.Append(heading).Append("\n\n");
        foreach (var item in arr.EnumerateArray())
            sb.Append("> ").Append(item.GetString()).Append('\n');
        sb.Append('\n');
    }

    private static string EscapeTableCell(string s) =>
        s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");

    private static string FirstLine(string msg)
    {
        var nl = msg.IndexOfAny(['\n', '\r']);
        return nl > 0 ? msg[..nl] : msg;
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
            ["standard"] = new TierMetrics(),
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
