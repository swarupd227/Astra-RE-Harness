using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.b multi-stage orchestrator. Creates one DocGenerationRun
/// row, backgrounds a Task that walks the requested stages in order
/// (routine-summary → module → overview), and updates the run row's
/// State + Summary + MetricsJson incrementally so the UI can poll
/// progress per stage.
///
/// Stage dependencies are enforced: requesting "module" without
/// "routine-summary" is OK if routine-summary sections already exist
/// for the corpus; otherwise the orchestrator fails the stage with a
/// clear message rather than emitting bland sections from missing input.
/// </summary>
public sealed class DocsGenerationOrchestrator
{
    public static readonly string[] DefaultStages = { "routine-summary", "module", "overview" };
    private static readonly HashSet<string> ValidStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "routine-summary", "module", "overview",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RoutineSummaryPipeline _summaryPipeline;
    private readonly HierarchicalRollupPipeline _rollupPipeline;
    private readonly ILogger<DocsGenerationOrchestrator> _logger;

    public DocsGenerationOrchestrator(
        IServiceScopeFactory scopeFactory,
        RoutineSummaryPipeline summaryPipeline,
        HierarchicalRollupPipeline rollupPipeline,
        ILogger<DocsGenerationOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _summaryPipeline = summaryPipeline;
        _rollupPipeline = rollupPipeline;
        _logger = logger;
    }

    public sealed record GenerateOptions(IReadOnlyList<string> Stages, int? Take, bool Force);

    public async Task<Guid> StartAsync(Guid corpusId, GenerateOptions opts, CancellationToken ct)
    {
        if (opts.Stages.Count == 0)
            throw new ArgumentException("At least one stage required.");
        foreach (var s in opts.Stages)
            if (!ValidStages.Contains(s))
                throw new ArgumentException($"Unknown stage '{s}'. Valid: {string.Join(", ", ValidStages)}.");

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
                StagesRequested = string.Join(",", opts.Stages),
                State = "QUEUED",
                Summary = $"Queued: {opts.Stages.Count} stage(s)",
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.DocGenerationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            runId = run.Id;
        }

        _ = Task.Run(() => RunPipelineAsync(runId, corpusId, sourceVersionId, opts));
        return runId;
    }

    private async Task RunPipelineAsync(Guid runId, Guid corpusId, Guid sourceVersionId, GenerateOptions opts)
    {
        var allStageMetrics = new Dictionary<string, object?>();
        try
        {
            foreach (var stage in opts.Stages)
            {
                await UpdateAsync(runId, "RUNNING", $"Running stage: {stage}", null);
                var ct = CancellationToken.None;
                switch (stage.ToLowerInvariant())
                {
                    case "routine-summary":
                    {
                        var r = await _summaryPipeline.RunRoutineSummaryStageAsync(
                            runId, corpusId, sourceVersionId,
                            new RoutineSummaryPipeline.GenerateOptions(opts.Take, opts.Force), ct);
                        allStageMetrics["routine-summary"] = ParseMetrics(r.MetricsJson);
                        break;
                    }
                    case "module":
                    {
                        var (succ, fail) = await _rollupPipeline.RunModuleStageAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["module"] = new { sections = succ, failed = fail };
                        break;
                    }
                    case "overview":
                    {
                        var (succ, fail) = await _rollupPipeline.RunOverviewStageAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["overview"] = new { sections = succ, failed = fail };
                        break;
                    }
                }
                await UpdateAsync(runId, "RUNNING", $"Completed stage: {stage}", allStageMetrics);
            }

            await UpdateAsync(runId, "SUCCEEDED", $"All {opts.Stages.Count} stage(s) complete", allStageMetrics, completed: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docs orchestrator run {RunId} crashed", runId);
            await FailAsync(runId, ex.Message);
        }
    }

    private async Task UpdateAsync(Guid runId, string state, string summary, IDictionary<string, object?>? metrics, bool completed = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.DocGenerationRuns.FirstOrDefaultAsync(r => r.Id == runId);
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
        var row = await db.DocGenerationRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (row is null) return;
        row.State = "FAILED";
        row.ErrorSummary = message.Length > 4000 ? message[..4000] : message;
        row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static object? ParseMetrics(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        }
        catch { return json; }
    }
}
