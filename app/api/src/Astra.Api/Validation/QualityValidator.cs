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

    /// <summary>The AI review must leave room inside App Service's 230 s request limit for the synchronous route.</summary>
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromSeconds(150);

    public QualityValidator(AppDbContext db, IBlobClient blob, StorageOptions storage, IAuditLogger audit, ILogger<QualityValidator> log,
        QualityReviewer? reviewer = null)
    {
        _reviewer = reviewer;
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
            QualityReviewResult? review = null;
            if (aiReview && _reviewer is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReviewTimeout);
                review = await _reviewer.ReviewAsync(_db, scaffold, scaffold.Spec?.Subroutine?.Name ?? "",
                    scaffold.Spec?.SpecJson.RootElement.GetRawText(), files, timeout.Token);
                findings.AddRange(review.Findings);
                findings = findings
                    .OrderBy(f => Array.IndexOf(QualityRules.Severities, f.Severity))
                    .ThenBy(f => f.Source == "rule" ? 0 : 1)
                    .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line ?? 0).ToList();
            }
            var summary = QualityRules.Summarise(findings);
            // The verdict and its sentence come from the rules; the reviewer's findings are counted separately.
            var ruleSummary = QualityRules.Summarise(findings.Where(f => f.Source == "rule").ToList());

            var report = new StringBuilder();
            report.AppendLine($"=== quality === {scaffold.TargetPlatform} · {files.Count} files · {findings.Count} findings · {summary.Blocking} blocking");
            foreach (var s in QualityRules.Severities) report.AppendLine($"  {s}: {summary.BySeverity[s]}");
            report.AppendLine();
            if (review is not null)
                report.AppendLine($"  AI review: {review.Status} · {review.Findings.Count} kept of {review.Proposed} proposed{(review.Note is null ? "" : $" · {review.Note}")}");
            foreach (var f in findings)
            {
                report.AppendLine(QualityRules.FormatLine(f) + (f.Source == "ai" ? "  [AI review, advisory]" : ""));
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
