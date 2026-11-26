using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Astra.Api.Validation.Quality;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// The QUALITY gate: reads a generated package for security problems, coding-standard breaches and maintainability
/// issues (<see cref="QualityRules"/>) and records a <see cref="ValidationRun"/> with stage = QUALITY. Blocker and
/// critical findings fail it; everything else passes with the findings listed, so a reviewer sees them and a
/// regeneration can fix them. Same lifecycle as the other gates: a RUNNING row, an audited start and end, a report
/// in blob storage (one <c>QUALITY file:line RULE severity: message</c> line per finding, which
/// <see cref="GateFailureDigest"/> reads back for a repair) and the counts plus the findings in the metrics.
/// It reads the package only — nothing is built or run — so it is cheap and safe to run on the live API.
/// </summary>
public sealed class QualityValidator
{
    public const string Stage = "QUALITY";
    private const int MaxFindingsInMetrics = 300;

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<QualityValidator> _log;
    private readonly QualityReviewer? _reviewer;
    private readonly MavenClient? _maven;
    private readonly IHttpClientFactory? _httpFactory;

    /// <summary>The Java tools (SpotBugs, PMD, Checkstyle) run in the maven sidecar beside the AI review.</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(180);

    /// <summary>The AI review must leave room inside App Service's 230 s request limit for the synchronous route.</summary>
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromSeconds(150);

    public QualityValidator(AppDbContext db, IBlobClient blob, StorageOptions storage, IAuditLogger audit, ILogger<QualityValidator> log,
        QualityReviewer? reviewer = null, MavenClient? maven = null, IHttpClientFactory? httpFactory = null)
    {
        _reviewer = reviewer;
        _maven = maven;
        _httpFactory = httpFactory;
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _log = log;
    }

    /// <param name="aiReview">Also ask the reviewer agent (advisory findings, one model call). The auto-fix loop
    /// leaves it off between rounds; the gate run from the report or a thread has it on.</param>
    public async Task<ValidationRun> RunAsync(Guid scaffoldId, DevPersonaContext? actor, CancellationToken ct, bool aiReview = true)
    {
        var scaffold = await _db.Scaffolds.Include(s => s.Spec).ThenInclude(sp => sp!.Subroutine).FirstOrDefaultAsync(s => s.Id == scaffoldId, ct)
            ?? throw new InvalidOperationException($"Scaffold {scaffoldId} not found.");

        var run = new ValidationRun
        {
            Id = Guid.NewGuid(),
            ScaffoldId = scaffold.Id,
            SpecId = scaffold.SpecId,
            Stage = Stage,
            Status = "RUNNING",
            Summary = "Quality checks queued",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.ValidationRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("validation.started", "scaffold", scaffold.Id, actor,
            payload: new { runId = run.Id, stage = run.Stage, targetPlatform = scaffold.TargetPlatform }, ct: ct);

        try
        {
            var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
            using var manifest = JsonDocument.Parse(manifestText);
            var files = manifest.RootElement.GetProperty("files").EnumerateArray()
                .Select(f => (Path: f.GetProperty("path").GetString() ?? "", Content: f.GetProperty("content").GetString() ?? ""))
                .Where(f => f.Path.Length > 0)
                .ToList();

            var findings = QualityRules.Scan(files).ToList();

            // The Java tools, the Python tools, the dependency check and the AI review run side by side, each within
            // its own time limit.
            var toolsTask = RunJavaToolsAsync(scaffold.TargetPlatform, files, ct);
            var pythonTask = PythonTools.RunAsync(files, ct);
            var deps = Dependencies.Read(files);
            var depsTask = CheckDependenciesAsync(deps, ct);
            Task<QualityReviewResult>? reviewTask = null;
            CancellationTokenSource? reviewTimeout = null;
            if (aiReview && _reviewer is not null)
            {
                reviewTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reviewTimeout.CancelAfter(ReviewTimeout);
                reviewTask = _reviewer.ReviewAsync(_db, scaffold, scaffold.Spec?.Subroutine?.Name ?? "",
                    scaffold.Spec?.SpecJson.RootElement.GetRawText(), files, reviewTimeout.Token);
            }
            var tools = await toolsTask;
            findings.AddRange(tools.Findings);
            var runs = new Dictionary<string, MavenClient.ToolRun>(tools.Runs);
            var (pyFindings, pyRuns) = await pythonTask;
            findings.AddRange(pyFindings);
            foreach (var (k, v) in pyRuns) runs[k] = v;
            var (depFindings, depRan, depError) = await depsTask;
            findings.AddRange(depFindings);
            if (deps.Count > 0)
                runs["dependencies"] = new(depRan, depFindings.Count, 0,
                    depRan ? null : $"the OSV.dev lookup did not complete ({depError})");

            // The bill of materials (CycloneDX) for what the package declares, kept with the run.
            string? sbomUri = null;
            if (deps.Count > 0)
            {
                var sbom = Dependencies.BuildSbom(scaffold.Spec?.Subroutine?.Name ?? scaffold.Id.ToString(), scaffold.GeneratedAt.ToString("yyyyMMddHHmmss"), deps, DateTimeOffset.UtcNow);
                sbomUri = await _blob.PutTextAsync(_storage.Buckets.Scaffolds, $"validation/{run.Id:N}/sbom.cdx.json", sbom, "application/json", ct);
            }
            QualityReviewResult? review = null;
            if (reviewTask is not null)
            {
                review = await reviewTask;
                reviewTimeout!.Dispose();
                findings.AddRange(review.Findings);
            }
            findings = findings
                .OrderBy(f => Array.IndexOf(QualityRules.Severities, f.Severity))
                .ThenBy(f => f.Source switch { "rule" => 0, "tool" => 1, _ => 2 })
                .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line ?? 0).ToList();
            var summary = QualityRules.Summarise(findings);
            // The verdict and its sentence come from the deterministic checks (rules and tools); the reviewer's
            // findings are counted separately.
            var ruleSummary = QualityRules.Summarise(findings.Where(f => f.Source != "ai").ToList());

            var report = new StringBuilder();
            report.AppendLine($"=== quality === {scaffold.TargetPlatform} · {files.Count} files · {findings.Count} findings · {summary.Blocking} blocking");
            foreach (var s in QualityRules.Severities) report.AppendLine($"  {s}: {summary.BySeverity[s]}");
            report.AppendLine();
            foreach (var (name, t) in runs)
                report.AppendLine($"  {name}: {(t.Ok ? $"{t.Count} findings in {t.DurationMs} ms" : $"did not run ({t.Error})")}");
            if (review is not null)
                report.AppendLine($"  AI review: {review.Status} · {review.Findings.Count} kept of {review.Proposed} proposed{(review.Note is null ? "" : $" · {review.Note}")}");
            foreach (var f in findings)
            {
                report.AppendLine(QualityRules.FormatLine(f) + (f.Source == "ai" ? "  [AI review, advisory]" : f.Source == "tool" ? "  [tool]" : ""));
                report.AppendLine($"    why: {f.Why}");
                report.AppendLine($"    fix: {f.Fix}");
            }
            report.AppendLine();
            report.AppendLine($"=== verdict === {(ruleSummary.Passed ? "PASSED" : "FAILED")}");

            run.LogBlobUri = await _blob.PutTextAsync(_storage.Buckets.Scaffolds, $"validation/{run.Id:N}/quality.log", report.ToString(), ct: ct);
            run.MetricsJson = JsonSerializer.Serialize(new
            {
                total = summary.Total,
                blocking = summary.Blocking,
                bySeverity = summary.BySeverity,
                byCategory = summary.ByCategory,
                fileCount = files.Count,
                blockOn = QualityRules.BlockingByDefault.OrderBy(s => Array.IndexOf(QualityRules.Severities, s)).ToArray(),
                truncated = findings.Count > MaxFindingsInMetrics,
                findings = findings.Take(MaxFindingsInMetrics).Select(f => new
                {
                    rule = f.Rule, category = f.Category, severity = f.Severity, file = f.File, line = f.Line,
                    message = f.Message, why = f.Why, fix = f.Fix, source = f.Source, specClaimId = f.SpecClaimId,
                }),
                sbom = sbomUri is null ? null : new { components = deps.Count, ecosystems = deps.Select(d => d.Ecosystem).Distinct().ToArray() },
                sbomUri,
                tools = runs.ToDictionary(kv => kv.Key, kv => new { ok = kv.Value.Ok, count = kv.Value.Count, durationMs = kv.Value.DurationMs, error = kv.Value.Error }),
                aiReview = review is null ? null : new
                {
                    status = review.Status, kept = review.Findings.Count, proposed = review.Proposed, dropped = review.Dropped,
                    model = review.Model, llmCallId = review.LlmCallId, costUsd = review.CostUsd, note = review.Note,
                },
            });
            run.Status = ruleSummary.Passed ? "PASSED" : "FAILED";
            run.ErrorCode = ruleSummary.Passed ? null : "quality.blocking_findings";
            run.Summary = SummaryLine(ruleSummary) + (review is { Status: "reviewed" } r
                ? $" · AI review: {r.Findings.Count} finding{(r.Findings.Count == 1 ? "" : "s")}"
                : "");
            run.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Quality gate crashed for scaffold {Scaffold}", scaffold.Id);
            run.Status = "ERRORED";
            run.ErrorCode = "quality.runner_crashed";
            run.Summary = $"Quality checks could not run: {ex.Message}";
            run.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        await _audit.LogAsync("validation.completed", "scaffold", scaffold.Id, actor,
            payload: new { runId = run.Id, stage = run.Stage, status = run.Status, summary = run.Summary }, ct: CancellationToken.None);
        return run;
    }

    private async Task<(IReadOnlyList<QualityFinding>, bool, string?)> CheckDependenciesAsync(IReadOnlyList<Dependency> deps, CancellationToken ct)
    {
        if (deps.Count == 0 || _httpFactory is null) return ([], true, null);
        var http = _httpFactory.CreateClient("osv");
        http.Timeout = TimeSpan.FromSeconds(60);
        return await Dependencies.CheckAsync(http, deps, ct);
    }

    /// <summary>What the Java tools found, as quality findings (paths back in the package's own layout).</summary>
    internal sealed record ToolsResult(IReadOnlyList<QualityFinding> Findings, IReadOnlyDictionary<string, MavenClient.ToolRun> Runs)
    {
        public static readonly ToolsResult None = new([], new Dictionary<string, MavenClient.ToolRun>());
    }

    private async Task<ToolsResult> RunJavaToolsAsync(string targetPlatform, IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        if (_maven is null) return ToolsResult.None;
        // java-spring is a Maven project at the root; angular-java keeps it under backend/.
        string? prefix = string.Equals(targetPlatform, "java-spring", StringComparison.OrdinalIgnoreCase) ? ""
            : string.Equals(targetPlatform, "angular-java", StringComparison.OrdinalIgnoreCase) ? "backend/" : null;
        if (prefix is null) return ToolsResult.None;
        var sources = files
            .Where(f => f.Path.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => new MavenClient.JavaSource(f.Path.Replace('\\', '/')[prefix.Length..], f.Content))
            .ToList();
        if (!sources.Any(s => s.Path == "pom.xml")) return ToolsResult.None;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ToolTimeout);
            var r = await _maven.AnalyzeAsync(sources, timeout.Token);
            if (r is null)
                return new([], new Dictionary<string, MavenClient.ToolRun> { ["java-tools"] = new(false, 0, 0, "the Java build service predates static analysis") });
            return new(ToFindings(r.Findings, prefix), r.Tools);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Java static analysis failed");
            return new([], new Dictionary<string, MavenClient.ToolRun> { ["java-tools"] = new(false, 0, 0, ex.Message) });
        }
    }

    internal static IReadOnlyList<QualityFinding> ToFindings(IEnumerable<MavenClient.ToolFinding> found, string prefix) =>
        found
            .Where(f => QualityRules.Severities.Contains(f.Severity))
            .Select(f => new QualityFinding(f.Rule, f.Category, f.Severity, prefix + f.File, f.Line,
                f.Message.Trim(), ToolWhy(f), ToolFix(f), Source: "tool"))
            .ToList();

    private static string ToolWhy(MavenClient.ToolFinding f) => f.Tool switch
    {
        "spotbugs" => f.Category == "security"
            ? "FindSecBugs traced this pattern in the compiled code; it is a known route to an exploitable weakness."
            : "SpotBugs found this in the compiled code; patterns like it cause wrong results or crashes at run time.",
        "pmd" => f.Category == "maintainability"
            ? "PMD measured the code past the size or complexity limit; such code is hard to review and change safely."
            : "PMD flags this as error-prone or against Java best practice.",
        "checkstyle" => "It breaks the Java conventions a reviewer expects, which makes the code harder to read and maintain.",
        _ => "Reported by static analysis.",
    };

    private static string ToolFix(MavenClient.ToolFinding f) => f.Tool switch
    {
        "spotbugs" => $"Change the code so `{f.Rule.Replace("SPOTBUGS-", "")}` no longer applies (see the SpotBugs/FindSecBugs description).",
        "pmd" => $"Follow PMD's `{f.Rule.Replace("PMD-", "")}` rule.",
        "checkstyle" => $"Follow Checkstyle's `{f.Rule.Replace("CHECKSTYLE-", "")}` convention.",
        _ => "Address the finding.",
    };

    /// <summary>"No blocking findings · 2 major, 5 minor" / "3 blocking findings (1 critical security) · …".</summary>
    internal static string SummaryLine(QualitySummary s)
    {
        if (s.Total == 0) return "No findings — security, standards and maintainability checks all clean";
        var parts = QualityRules.Severities.Where(x => s.BySeverity[x] > 0).Select(x => $"{s.BySeverity[x]} {x}");
        var counts = string.Join(", ", parts);
        return s.Passed
            ? $"No blocking findings · {counts}"
            : $"{s.Blocking} blocking finding{(s.Blocking == 1 ? "" : "s")} · {counts}";
    }
}
