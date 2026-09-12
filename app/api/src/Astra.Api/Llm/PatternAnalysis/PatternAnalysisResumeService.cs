using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// After boot, resume any pattern-analysis run the previous process left
/// RESUMABLE (the orphaned-run cleanup in Program.cs converts interrupted
/// RUNNING rows to that state). The digests already written are skipped,
/// so a restart costs seconds, not the hours it used to.
/// </summary>
public sealed class PatternAnalysisResumeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PatternAnalysisOrchestrator _orchestrator;
    private readonly bool _enabled;
    private readonly ILogger<PatternAnalysisResumeService> _logger;

    public PatternAnalysisResumeService(
        IServiceScopeFactory scopeFactory,
        PatternAnalysisOrchestrator orchestrator,
        IConfiguration cfg,
        ILogger<PatternAnalysisResumeService> logger)
    {
        _scopeFactory = scopeFactory;
        _orchestrator = orchestrator;
        _enabled = cfg.GetValue("Llm:PatternAnalysis:AutoResume", true);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        try
        {
            // Let the schema bootstrap, seeds and provider wiring settle first.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            List<Guid> resumable;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (!await db.Database.CanConnectAsync(stoppingToken)) return;
                resumable = await db.PatternAnalysisRuns.AsNoTracking()
                    .Where(r => r.State == "RESUMABLE")
                    .OrderBy(r => r.StartedAt)
                    .Select(r => r.Id)
                    .ToListAsync(stoppingToken);
            }

            foreach (var runId in resumable)
            {
                if (await _orchestrator.ResumeAsync(runId))
                    _logger.LogInformation("Resumed pattern-analysis run {RunId} after restart", runId);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pattern-analysis auto-resume failed; runs stay RESUMABLE for manual resume");
        }
    }
}
