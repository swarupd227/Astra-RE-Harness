using System.Diagnostics;
using System.Text.Json;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation.Quality;

/// <summary>Loads the records a <see cref="QualityReport"/> is built from and renders it (JSON, HTML or PDF).</summary>
public sealed class QualityReportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<QualityReportService> _log;

    public QualityReportService(AppDbContext db, ILogger<QualityReportService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<QualityReport?> BuildAsync(Guid scaffoldId, CancellationToken ct)
    {
        var scaffold = await _db.Scaffolds.AsNoTracking()
            .Include(s => s.Spec).ThenInclude(sp => sp!.Subroutine).ThenInclude(r => r!.SourceFile).ThenInclude(f => f!.SourceVersion).ThenInclude(v => v!.Corpus)
            .FirstOrDefaultAsync(s => s.Id == scaffoldId, ct);
        if (scaffold is null) return null;
        var signature = await _db.Signatures.AsNoTracking().Where(s => s.SpecId == scaffold.SpecId)
            .OrderByDescending(s => s.SignedAt).FirstOrDefaultAsync(ct);
        var runs = await _db.ValidationRuns.AsNoTracking().Where(r => r.ScaffoldId == scaffoldId)
            .OrderByDescending(r => r.StartedAt).Take(200)
            .Select(r => new QualityReportInput.Run(r.Id, r.Stage, r.Status, r.Summary, r.MetricsJson, r.StartedAt, r.CompletedAt))
            .ToListAsync(ct);
        var spec = scaffold.Spec;
        var routine = spec?.Subroutine;
        return QualityReportBuilder.Build(new QualityReportInput(
            scaffold.Id, scaffold.SpecId, routine?.Name ?? scaffold.Id.ToString(), routine?.SourceLanguage, routine?.SourceFile?.RelativePath,
            routine?.SourceFile?.SourceVersion?.Corpus?.Name, scaffold.TargetPlatform, scaffold.GeneratedAt,
            scaffold.FileCount, scaffold.TotalLines, scaffold.TodoCount, scaffold.GitCommitHash,
            spec?.State ?? "UNKNOWN", ClaimCount(spec?.SpecJson), signature?.SignerDisplay, signature?.SignedAt, signature?.SpecCanonicalHash,
            runs, Endpoints.HealthEndpoints.Build, DateTimeOffset.UtcNow));
    }

    /// <summary>The latest package of every routine in a programme that has generated code, newest first.</summary>
    public async Task<IReadOnlyList<Guid>> LatestScaffoldsForCorpusAsync(Guid corpusId, CancellationToken ct) =>
        (await _db.Scaffolds.AsNoTracking()
            .Where(s => s.Spec!.Subroutine!.SourceFile!.SourceVersion!.CorpusId == corpusId)
            .Select(s => new { s.Id, s.Spec!.SubroutineId, s.GeneratedAt })
            .ToListAsync(ct))
        .GroupBy(s => s.SubroutineId).Select(g => g.OrderByDescending(s => s.GeneratedAt).First())
        .OrderByDescending(s => s.GeneratedAt).Select(s => s.Id).ToList();

    private static int ClaimCount(JsonDocument? spec) =>
        spec is null ? 0 : Astra.Api.Conversations.ConversationService.CountClaims(spec.RootElement);

    /// <summary>HTML → PDF with weasyprint (in the API image, as for the documentation export).</summary>
    public async Task<byte[]> RenderPdfAsync(string html, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"astra-quality-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "report.html");
        var output = Path.Combine(dir, "report.pdf");
        try
        {
            await File.WriteAllTextAsync(input, html, ct);
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("weasyprint")
                {
                    ArgumentList = { input, output },
                    RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                },
            };
            p.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            _ = p.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* already gone */ } throw; }
            if (p.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"weasyprint exited {p.ExitCode}: {(await stderr).Trim()}");
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException ex) { _log.LogDebug(ex, "Could not clear {Dir}", dir); }
        }
    }
}
