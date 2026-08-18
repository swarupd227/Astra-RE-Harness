using System.IO.Compression;
using System.Text.Json;
using Astra.Api.Docs;
using Astra.Api.Endpoints;
using Astra.Api.Llm.Dependency;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Export;

/// <summary>
/// Project-level artifact bundle: one zip with everything the harness has
/// produced for a corpus.
///
/// Always included (when present):
///   manifest.json                              — what's in the bundle + corpus metadata
///   docs/                                      — MkDocs-Material tree (same as docs export)
///   dependency-graph/dependency-graph.json     — same shape as GET /corpora/{id}/dependency-graph
///   migration-plan/migration-plan.json         — same shape as GET /corpora/{id}/migration-plan
///   pattern-analysis/pattern-analysis.json     — same shape as GET /corpora/{id}/pattern-clusters
///
/// With includeSources=true (heavyweight — blob-backed):
///   sources/{relativePath}                     — original source tree, latest version
///   scaffolds/{routine}-{id8}/{path}           — latest scaffold package per spec
///   validation-logs/{routine}-{id8}/{stage}-{status}-{run8}.log
///
/// Artifacts that don't exist yet are simply absent from the zip; the
/// manifest records what made it in. Unreadable blobs are logged and
/// skipped rather than failing the whole export.
/// </summary>
public sealed class ProjectExportService
{
    private readonly AppDbContext _db;
    private readonly DependencyGraphBuilder _graphBuilder;
    private readonly IBlobClient _blob;
    private readonly ILogger<ProjectExportService> _logger;

    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ProjectExportService(
        AppDbContext db,
        DependencyGraphBuilder graphBuilder,
        IBlobClient blob,
        ILogger<ProjectExportService> logger)
    {
        _db = db;
        _graphBuilder = graphBuilder;
        _blob = blob;
        _logger = logger;
    }

    public async Task<DocExportService.ExportResult> ExportAsync(
        Guid corpusId, bool includeSources, CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");

        var contents = new Dictionary<string, object>();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Guard against duplicate entry names — ZipArchive happily writes
            // duplicates, which some extractors reject.
            var written = new HashSet<string>(StringComparer.Ordinal);

            // ── Docs (same section filter as DocExportService.ExportAsync) ──
            var sections = await _db.DocSections.AsNoTracking()
                .Where(s => s.CorpusId == corpusId
                         && (s.State == "SIGNED" || s.State == "STALE" || s.State == "DRAFT"))
                .Include(s => s.Subroutine)
                .ToListAsync(ct);
            if (sections.Count > 0)
            {
                DocExportService.WriteMkDocsTree(zip, "docs/", corpus, sections);
                contents["docs"] = new { sectionCount = sections.Count };
            }

            // ── Dependency graph ──
            var graph = await _graphBuilder.BuildAsync(corpusId, ct);
            if (graph is not null)
            {
                WriteJson(zip, written, "dependency-graph/dependency-graph.json",
                    DependencyEndpoints.RenderGraph(graph));
                contents["dependencyGraph"] = new
                {
                    nodeCount = graph.Stats.NodeCount,
                    callEdgeCount = graph.Stats.CallEdgeCount,
                    sharedStorageEdgeCount = graph.Stats.SharedStorageEdgeCount,
                };
            }

            // ── Migration plan (current = approved, else latest draft) ──
            var plan = await _db.MigrationPlans.AsNoTracking()
                .Where(p => p.CorpusId == corpusId && p.Status != "archived")
                .OrderByDescending(p => p.Status == "approved")
                .ThenByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (plan is not null)
            {
                WriteJson(zip, written, "migration-plan/migration-plan.json",
                    await MigrationPlanEndpoints.BuildDetailAsync(_db, plan, ct));
                contents["migrationPlan"] = new
                {
                    status = plan.Status,
                    strategyName = plan.StrategyName,
                    totalWaves = plan.TotalWaves,
                    totalRoutines = plan.TotalRoutines,
                };
            }

            // ── Pattern analysis (latest completed run + its clusters) ──
            var latestRun = await _db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == corpusId && (r.State == "SUCCEEDED" || r.State == "PARTIAL"))
                .OrderByDescending(r => r.CompletedAt)
                .FirstOrDefaultAsync(ct);
            if (latestRun is not null)
            {
                var clusters = await _db.PatternClusters.AsNoTracking()
                    .Where(c => c.PatternAnalysisRunId == latestRun.Id)
                    .OrderByDescending(c => c.MemberCount)
                    .ThenBy(c => c.Label)
                    .ToListAsync(ct);
                WriteJson(zip, written, "pattern-analysis/pattern-analysis.json", new
                {
                    run = PatternAnalysisEndpoints.RenderRun(latestRun),
                    clusters = clusters.Select(PatternAnalysisEndpoints.RenderCluster),
                });
                contents["patternAnalysis"] = new { state = latestRun.State, clusterCount = clusters.Count };
            }

            // ── Heavyweight blob-backed artifacts ──
            if (includeSources)
            {
                var stats = await WriteSourcesAsync(zip, written, corpus, ct);
                contents["sources"] = new { fileCount = stats.SourceFiles };
                contents["scaffolds"] = new { packageCount = stats.Scaffolds, fileCount = stats.ScaffoldFiles };
                contents["validationLogs"] = new { logCount = stats.ValidationLogs };
            }

            WriteJson(zip, written, "manifest.json", new
            {
                project = corpus.Name,
                corpusId = corpus.Id,
                state = corpus.State,
                sourceType = corpus.SourceType,
                sourceUrl = corpus.SourceUrl,
                branch = corpus.Branch,
                fileCount = corpus.FileCount,
                totalLoc = corpus.TotalLoc,
                exportedAt = DateTimeOffset.UtcNow,
                includeSources,
                contents,
            });
        }

        _logger.LogInformation(
            "Project export: corpus={CorpusId} includeSources={IncludeSources} bytes={Bytes}",
            corpusId, includeSources, ms.Length);

        return new DocExportService.ExportResult(
            $"{DocExportService.Slug(corpus.Name)}-artifacts.zip",
            "application/zip",
            ms.ToArray());
    }

    private sealed record SourceStats(int SourceFiles, int Scaffolds, int ScaffoldFiles, int ValidationLogs);

    private async Task<SourceStats> WriteSourcesAsync(
        ZipArchive zip, HashSet<string> written, Corpus corpus, CancellationToken ct)
    {
        var version = corpus.LatestVersionId is Guid vid
            ? await _db.SourceVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct)
            : await _db.SourceVersions.AsNoTracking()
                .Where(v => v.CorpusId == corpus.Id)
                .OrderByDescending(v => v.IngestedAt)
                .FirstOrDefaultAsync(ct);
        if (version is null) return new SourceStats(0, 0, 0, 0);

        // Original source tree.
        var files = await _db.SourceFiles.AsNoTracking()
            .Where(f => f.SourceVersionId == version.Id)
            .OrderBy(f => f.RelativePath)
            .ToListAsync(ct);
        var sourceCount = 0;
        foreach (var f in files)
        {
            var text = await TryGetBlobAsync(f.BlobUri, $"source file {f.RelativePath}", ct);
            if (text is null) continue;
            if (TryWriteText(zip, written, $"sources/{SafeRelPath(f.RelativePath)}", text))
                sourceCount++;
        }

        // Latest scaffold package per spec for this source version.
        var scaffoldRows = await (
            from sc in _db.Scaffolds.AsNoTracking()
            join sp in _db.Specs.AsNoTracking() on sc.SpecId equals sp.Id
            join sub in _db.Subroutines.AsNoTracking() on sp.SubroutineId equals sub.Id
            where sp.SourceVersionId == version.Id
            select new { Scaffold = sc, SubName = sub.Name })
            .ToListAsync(ct);
        var latestPerSpec = scaffoldRows
            .GroupBy(x => x.Scaffold.SpecId)
            .Select(g => g.OrderByDescending(x => x.Scaffold.GeneratedAt).First())
            .OrderBy(x => x.SubName, StringComparer.Ordinal)
            .ToList();

        var scaffoldCount = 0;
        var scaffoldFileCount = 0;
        var folderByScaffoldId = new Dictionary<Guid, string>();
        foreach (var row in latestPerSpec)
        {
            var folder = $"{DocExportService.Slug(row.SubName)}-{row.Scaffold.Id.ToString("N")[..8]}";
            folderByScaffoldId[row.Scaffold.Id] = folder;

            var manifestText = await TryGetBlobAsync(
                row.Scaffold.PackageBlobUri, $"scaffold package for {row.SubName}", ct);
            if (manifestText is null) continue;

            var filesWritten = 0;
            try
            {
                using var doc = JsonDocument.Parse(manifestText);
                if (doc.RootElement.TryGetProperty("files", out var filesEl)
                    && filesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fileEl in filesEl.EnumerateArray())
                    {
                        var path = fileEl.TryGetProperty("path", out var p) ? p.GetString() : null;
                        var content = fileEl.TryGetProperty("content", out var c) ? c.GetString() : null;
                        if (string.IsNullOrEmpty(path) || content is null) continue;
                        if (TryWriteText(zip, written, $"scaffolds/{folder}/{SafeRelPath(path)}", content))
                            filesWritten++;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Project export: scaffold manifest unparseable for {Sub}", row.SubName);
                continue;
            }

            if (filesWritten > 0)
            {
                scaffoldCount++;
                scaffoldFileCount += filesWritten;
            }
        }

        // Validation logs for those scaffolds.
        var scaffoldIds = latestPerSpec.Select(x => x.Scaffold.Id).ToList();
        var logCount = 0;
        if (scaffoldIds.Count > 0)
        {
            var runs = await _db.ValidationRuns.AsNoTracking()
                .Where(r => scaffoldIds.Contains(r.ScaffoldId) && r.LogBlobUri != null)
                .OrderBy(r => r.StartedAt)
                .ToListAsync(ct);
            foreach (var run in runs)
            {
                var text = await TryGetBlobAsync(run.LogBlobUri!, $"validation log {run.Id}", ct);
                if (text is null) continue;
                var folder = folderByScaffoldId.GetValueOrDefault(run.ScaffoldId, run.ScaffoldId.ToString("N")[..8]);
                var name = $"{run.Stage}-{run.Status}-{run.Id.ToString("N")[..8]}.log".ToLowerInvariant();
                if (TryWriteText(zip, written, $"validation-logs/{folder}/{name}", text))
                    logCount++;
            }
        }

        return new SourceStats(sourceCount, scaffoldCount, scaffoldFileCount, logCount);
    }

    private async Task<string?> TryGetBlobAsync(string blobUri, string what, CancellationToken ct)
    {
        try
        {
            return await _blob.GetTextAsync(blobUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Project export: skipping unreadable blob for {What} ({Uri})", what, blobUri);
            return null;
        }
    }

    private static void WriteJson(ZipArchive zip, HashSet<string> written, string path, object payload) =>
        TryWriteText(zip, written, path, JsonSerializer.Serialize(payload, JsonOpts));

    private static bool TryWriteText(ZipArchive zip, HashSet<string> written, string path, string content)
    {
        if (!written.Add(path)) return false;
        DocExportService.WriteZipEntry(zip, path, content);
        return true;
    }

    /// <summary>
    /// Normalises a stored relative path into a safe zip entry path:
    /// forward slashes, no leading separator, no "." / ".." segments.
    /// </summary>
    private static string SafeRelPath(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/')
            .Where(s => s.Length > 0 && s != "." && s != "..");
        var joined = string.Join('/', segments);
        return joined.Length > 0 ? joined : "unnamed";
    }
}
