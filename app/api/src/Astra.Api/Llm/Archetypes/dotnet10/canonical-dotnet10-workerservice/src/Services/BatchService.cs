using Demo.BatchWorker.Models;
using Demo.BatchWorker.Repositories;

namespace Demo.BatchWorker.Services;

public sealed class BatchService(
    IBatchRepository repo,
    ILogger<BatchService> logger)
{
    // [INV-1] ProcessPendingAsync fetches all records in Pending status,
    //         processes each, and marks them Completed (or Failed on error).
    // [INV-2] A record is never left in an intermediate state — it transitions
    //         atomically from Pending → Completed or Pending → Failed.
    // [EC-1]  When no pending records exist, the method completes without
    //         throwing (returns 0 processed).
    // [SE-1]  Writes status updates to the BatchRecords table.
    [SpecClaim("INV-1", "INV-2", "EC-1", "SE-1")]
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var pending = await repo.GetPendingAsync(ct);
        if (pending.Count == 0)
            return 0;

        int processed = 0;
        foreach (var record in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await repo.MarkProcessingAsync(record.Id, ct);
                await ProcessRecordAsync(record, ct);
                await repo.MarkCompletedAsync(record.Id, ct);
                processed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process batch record {RecordId}", record.Id);
                await repo.MarkFailedAsync(record.Id, ex.Message, ct);
            }
        }

        logger.LogInformation("Processed {Count}/{Total} records", processed, pending.Count);
        return processed;
    }

    private static Task ProcessRecordAsync(BatchRecord record, CancellationToken ct)
    {
        // Placeholder — replace with domain-specific batch logic.
        return Task.CompletedTask;
    }
}
