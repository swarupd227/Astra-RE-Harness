using Demo.BatchWorker.Services;

namespace Demo.BatchWorker.Workers;

/// <summary>
/// Replaces Windows ServiceBase.OnStart / OnStop and the Thread.Sleep polling loop.
/// ExecuteAsync owns the worker's entire lifetime; the CancellationToken is signalled
/// when the host is stopping so the worker can finish its current batch cleanly.
/// </summary>
public sealed class BatchProcessorWorker(
    BatchService batchService,
    IConfiguration configuration,
    ILogger<BatchProcessorWorker> logger) : BackgroundService
{
    // [INV-1] The worker processes one batch per tick. Tick interval is
    //         configurable via Worker:TickIntervalSeconds (default 30).
    // [OA-1]  Replaces System.Timers.Timer and Thread.Sleep loop.
    // [AP-1]  Fully async — no Thread.Sleep, no .Result/.Wait().
    [SpecClaim("INV-1", "OA-1", "AP-1")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue<int>("Worker:TickIntervalSeconds", 30);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("BatchProcessorWorker starting, tick every {Interval}", interval);

        // PeriodicTimer coalesces missed ticks — if ProcessBatchAsync takes longer
        // than the interval, the next tick fires immediately rather than queuing.
        await using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // [SE-1] Each tick creates a new BatchService scope via the factory.
                await batchService.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Propagate graceful shutdown — do not swallow.
                throw;
            }
            catch (Exception ex)
            {
                // Log but continue running — one bad batch should not kill the worker.
                logger.LogError(ex, "Unhandled error during batch processing tick");
            }
        }

        logger.LogInformation("BatchProcessorWorker stopped");
    }
}
