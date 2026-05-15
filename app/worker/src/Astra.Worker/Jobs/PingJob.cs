namespace Astra.Worker.Jobs;

/// <summary>
///     Heartbeat job — proves the Hangfire pipeline is alive.
///     Removed once real jobs (IngestSourceJob, ParseCorpusJob, ExtractSpecJob, ...)
///     come online in Phases B and C.
/// </summary>
public sealed class PingJob
{
    private readonly ILogger<PingJob> _logger;

    public PingJob(ILogger<PingJob> logger)
    {
        _logger = logger;
    }

    public Task ExecuteAsync()
    {
        _logger.LogInformation("PingJob fired at {Time:O}", DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
