using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Production routine-summary pipeline. All routines run single-shot with a
/// 4096-token output budget; headline tier (top
/// <see cref="DocsOptions.HeadlinePercentile"/> % by complexity-weighted
/// importance) goes to Opus, the rest to Sonnet.
///
/// WS6: calls go through <see cref="IDocWriter"/> (style guide + exemplar
/// as cached system blocks, retrying send, process-wide rate limiter, an
/// <see cref="LlmCall"/> row per routine, a working mock path). The
/// payload stays structured JSON — it is the base fact every rollup builds
/// on — but the rendered page is prose (<see cref="RoutineSummaryMarkdown"/>)
/// with a resolvable source citation, and the deterministic quality checks
/// land in <c>QualityJson</c>.
///
/// Retained from 11.0.a: bounded concurrency, idempotency (skip existing
/// sections for the same SourceVersion unless force), background mode,
/// incremental metrics.
/// </summary>
public sealed class RoutineSummaryPipeline
{
    public const string PromptId = "fortran-doc-summary";
    public const string PromptVersion = "v2.0";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly DocPromptAssets _assets;
    private readonly IDocWriter _writer;
    private readonly string _prompt;
    private readonly DocRunLogger _runLogger;
    private readonly ILogger<RoutineSummaryPipeline> _logger;

    public RoutineSummaryPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IWebHostEnvironment env,
        DocPromptAssets assets,
        IDocWriter writer,
        DocRunLogger runLogger,
        ILogger<RoutineSummaryPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _assets = assets;
        _writer = writer;
        _runLogger = runLogger;
        _logger = logger;
        _prompt = DocPromptAssets.ReadPrompt(env.ContentRootPath, "fortran-f77", "doc-summary.v1.md");
    }

    public sealed record GenerateOptions(int? Take, bool Force);

    public sealed record StageResult(int Succeeded, int Failed, int Skipped, string MetricsJson);

    /// <summary>
    /// Single-stage entry point: create a DocGenerationRun row and background
    /// the routine-summary work. The multi-stage orchestrator uses
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

        // Fire-and-forget: the request scope dies when the endpoint responds,
        // so the worker creates its own scope and token.
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
    /// Does not mark the run completed — the caller chains stages.
    /// </summary>
    public async Task<StageResult> RunRoutineSummaryStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, GenerateOptions opts, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
        var classifier = scope.ServiceProvider.GetRequiredService<RoutineTierClassifier>();

        await MarkAsync(db, runId, "RUNNING", "Loading corpus", null, ct);

        var tiers = await classifier.ClassifyForCorpusAsync(corpusId, sourceVersionId, ct);

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

        var tasks = todo.Select(sub =>
        {
            var assignment = tiers.TryGetValue(sub.Id, out var t) ? t : null;
            var tier = assignment?.Tier ?? "standard";
            var model = tier == "headline" ? _docsOpts.OpusModel : _docsOpts.SonnetModel;
            var callerNames = assignment?.CallerNames ?? Array.Empty<string>();
            return RunSingleAsync(sub, tier, model, callerNames, sem, runId, corpusId, sourceVersionId, metrics, blob, ct);
        }).ToList();

        await Task.WhenAll(tasks);

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
        MetricsAccumulator metrics, IBlobClient blob, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var path = sub.SourceFile?.RelativePath;
            var source = await blob.GetTextAsync(sub.SourceFile!.BlobUri, ct);
            var routineLines = DocSourceSlices.Slice(DocSourceSlices.SplitLines(source), sub.LineStart, sub.LineEnd, cap: 0);
            var user = BuildUserMessage(sub.Name, TryGetEnclosingModule(sub), path, tier, callerNames, routineLines);

            var request = new DocWriteRequest(
                PromptId, PromptVersion,
                _assets.SystemBlocks(_prompt, "routine-summary"),
                user,
                "emit_routine_summary", "Emit the routine's transition-documentation summary as structured fields.",
                RoutineSummaryToolSchema,
                model, 4096,
                "docs:routine-summary:" + PromptVersion,
                () => MockRoutineSummary(sub, tier, path));

            var result = await _writer.WriteAsync(request, ct);
            var payload = result.ToolInputJson;
            using (var check = JsonDocument.Parse(payload))
            {
                if (!check.RootElement.TryGetProperty("summary", out var s) || s.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(s.GetString()))
                    throw new InvalidOperationException("Routine summary has no 'summary' field.");
            }

            var rendered = RoutineSummaryMarkdown.Render(payload, sub.Name, path, sub.LineStart, sub.LineEnd);
            var checks = DocCriticPass.RunChecks(rendered, null, Array.Empty<string>(), isCatalog: false, requireCitations: false);
            var report = DocCriticPass.Report.ChecksOnly(checks, _writer.ProviderName, "routine summary: deterministic checks only");

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var call = DocLlmCalls.Record(db, _writer, request, result);
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
                RenderedMarkdown = rendered,
                LlmCallId = call.Id,
                QualityJson = report.ToJson(),
                GenerationRunId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.DocSections.Add(section);
            await db.SaveChangesAsync(ct);

            var u = result.Usage;
            metrics.Record(tier, 1, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens);
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
        string routineName, string? enclosing, string? path, string tier,
        IReadOnlyList<string> callerNames, string sourceLines)
    {
        var sb = new StringBuilder();
        sb.Append("routine_name: ").Append(routineName).Append('\n');
        sb.Append("enclosing_module: ").Append(enclosing ?? "null").Append('\n');
        if (!string.IsNullOrWhiteSpace(path))
            sb.Append("path: ").Append(path).Append('\n');
        sb.Append("tier: ").Append(tier).Append('\n');
        if (callerNames.Count > 0)
            sb.Append("callers: ").Append(string.Join(", ", callerNames.Take(10))).Append('\n');
        sb.Append("source:\n").Append(sourceLines);
        return sb.ToString();
    }

    private static string? TryGetEnclosingModule(Subroutine sub) =>
        sub.SourceFile?.RelativePath is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    private static string MockRoutineSummary(Subroutine sub, string tier, string? path)
    {
        var loc = Math.Max(1, sub.LineEnd - sub.LineStart + 1);
        return JsonSerializer.Serialize(new
        {
            id = $"rs.{sub.Name.ToLowerInvariant()}.v1",
            summary = $"{sub.Name} spans {loc} lines of {path ?? "its source file"} and was documented offline by the mock documentation provider, which interprets no source. Its contracts and traps stay unknown until a model-backed run replaces this placeholder.",
            inputs = new[] { "the routine's declared arguments (not interpreted offline)" },
            outputs = Array.Empty<string>(),
            sideEffects = Array.Empty<string>(),
            preconditions = Array.Empty<string>(),
            edgeCases = Array.Empty<string>(),
            tier,
            citations = new[] { new { lines = $"{sub.LineStart}-{sub.LineEnd}" } },
        });
    }

    // ── Tool input schema (structured-output contract) ──────────────────
    // Mirrors the doc-summary prompt's JSON shape. Only `summary` is required
    // so a pure routine with no side effects still validates.

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

    private static string FirstLine(string msg)
    {
        var nl = msg.IndexOfAny(new[] { '\n', '\r' });
        return nl > 0 ? msg[..nl] : msg;
    }

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
