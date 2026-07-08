namespace Demo.BatchWorker.Models;

public enum BatchStatus { Pending, Processing, Completed, Failed }

public sealed class BatchRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Payload { get; init; }
    public BatchStatus Status { get; set; } = BatchStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}
