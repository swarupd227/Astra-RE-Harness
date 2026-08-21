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
    public static readonly string[] DefaultStages = {
        "routine-summary", "module", "overview",
        "data-dictionary", "glossary", "interface", "business-rules",
        "sequence-diagram", "dependency-diagram",
    };
    private static readonly HashSet<string> ValidStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "routine-summary", "module", "overview",
        "data-dictionary", "glossary", "interface", "business-rules",
        "sequence-diagram", "dependency-diagram",
        // Phase B requirements pack. Deliberately absent from DefaultStages:
        // it is a separate deliverable for a different reader, requested
        // explicitly rather than produced by every docs run.
        "capability-map", "process-flow", "functional-requirement", "nfr",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RoutineSummaryPipeline _summaryPipeline;
    private readonly HierarchicalRollupPipeline _rollupPipeline;
    private readonly CatalogPipeline _catalogPipeline;
    private readonly DiagramPipeline _diagramPipeline;
    private readonly DocRunLogger _runLogger;
    private readonly ILogger<DocsGenerationOrchestrator> _logger;

    public DocsGenerationOrchestrator(
        IServiceScopeFactory scopeFactory,
        RoutineSummaryPipeline summaryPipeline,
        HierarchicalRollupPipeline rollupPipeline,
        CatalogPipeline catalogPipeline,
        DiagramPipeline diagramPipeline,
        DocRunLogger runLogger,
        ILogger<DocsGenerationOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _summaryPipeline = summaryPipeline;
        _rollupPipeline = rollupPipeline;
        _catalogPipeline = catalogPipeline;
        _diagramPipeline = diagramPipeline;
        _runLogger = runLogger;
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
        // Stages that produced NOTHING and recorded at least one failure —
        // the run must not report clean success over them (EnvestNet's
        // data-dictionary failed silently on every run this way).
        var failedStages = new List<string>();
        _runLogger.Log(runId, $"Run started — {opts.Stages.Count} stage(s): {string.Join(", ", opts.Stages)}");
        try
        {
            foreach (var stage in opts.Stages)
            {
                _runLogger.Log(runId, $"▶ Stage: {stage}");
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
                        if (r.Succeeded == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  routine-summary: {r.Succeeded} succeeded, {r.Failed} failed, {r.Skipped} skipped");
                        break;
                    }
                    case "module":
                    {
                        var (succ, fail) = await _rollupPipeline.RunModuleStageAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["module"] = new { sections = succ, failed = fail };
                        if (succ == 0 && fail > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  module: {succ} sections, {fail} failed");
                        break;
                    }
                    case "overview":
                    {
                        var (succ, fail) = await _rollupPipeline.RunOverviewStageAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["overview"] = new { sections = succ, failed = fail };
                        if (succ == 0 && fail > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  overview: {succ} sections, {fail} failed");
                        break;
                    }
                    case "data-dictionary":
                    {
                        var r = await _catalogPipeline.RunDataDictionaryAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["data-dictionary"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  data-dictionary: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "glossary":
                    {
                        var r = await _catalogPipeline.RunGlossaryAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["glossary"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  glossary: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "interface":
                    {
                        var r = await _catalogPipeline.RunInterfaceAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["interface"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  interface: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "business-rules":
                    {
                        var r = await _catalogPipeline.RunBusinessRulesAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["business-rules"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  business-rules: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    // ── Phase B: requirements pack (AS-IS) ──────────
                    // Opt-in via ?stages=... — these synthesise from the
                    // docs already generated, so they are cheap, but they
                    // are a different deliverable with a different reader.
                    case "capability-map":
                    {
                        var r = await _catalogPipeline.RunCapabilityMapAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["capability-map"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  capability-map: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "functional-requirement":
                    {
                        var r = await _catalogPipeline.RunFunctionalRequirementsAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["functional-requirement"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  functional-requirement: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "process-flow":
                    {
                        var r = await _catalogPipeline.RunProcessFlowsAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["process-flow"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  process-flow: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "nfr":
                    {
                        var r = await _catalogPipeline.RunNfrAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["nfr"] = new { entries = r.Entries, failed = r.Failed };
                        if (r.Entries == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  nfr: {r.Entries} entries, {r.Failed} failed");
                        break;
                    }
                    case "sequence-diagram":
                    {
                        var r = await _diagramPipeline.RunSequenceStageAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["sequence-diagram"] = new { diagrams = r.Diagrams, failed = r.Failed, skipped = r.Skipped };
                        if (r.Diagrams == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  sequence-diagram: {r.Diagrams} diagrams, {r.Failed} failed, {r.Skipped} skipped");
                        break;
                    }
                    case "dependency-diagram":
                    {
                        var r = await _diagramPipeline.RunDependencyStageAsync(
                            runId, corpusId, sourceVersionId, opts.Force, ct);
                        allStageMetrics["dependency-diagram"] = new { diagrams = r.Diagrams, failed = r.Failed, skipped = r.Skipped };
                        if (r.Diagrams == 0 && r.Failed > 0) failedStages.Add(stage);
                        _runLogger.Log(runId, $"  dependency-diagram: {r.Diagrams} diagrams, {r.Failed} failed, {r.Skipped} skipped");
                        break;
                    }
                }
                await UpdateAsync(runId, "RUNNING", $"Completed stage: {stage}", allStageMetrics);
            }

            if (failedStages.Count == 0)
            {
                _runLogger.Log(runId, $"✓ All {opts.Stages.Count} stage(s) complete.");
                await UpdateAsync(runId, "SUCCEEDED", $"All {opts.Stages.Count} stage(s) complete", allStageMetrics, completed: true);
            }
            else
            {
                var state = failedStages.Count == opts.Stages.Count ? "FAILED" : "PARTIAL";
                var summary =
                    $"{opts.Stages.Count - failedStages.Count} of {opts.Stages.Count} stage(s) complete; " +
                    $"failed: {string.Join(", ", failedStages)}";
                _runLogger.Log(runId, $"✗ {summary}");
                await UpdateAsync(runId, state, summary, allStageMetrics, completed: true);
            }
            _runLogger.Complete(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docs orchestrator run {RunId} crashed", runId);
            _runLogger.Log(runId, $"✗ Error: {ex.Message}");
            _runLogger.Complete(runId);
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
