using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;

namespace Astra.Api.Audit;

public sealed class PostgresAuditLogger : IAuditLogger
{
    private readonly AppDbContext _db;
    private readonly ILogger<PostgresAuditLogger> _logger;

    public PostgresAuditLogger(AppDbContext db, ILogger<PostgresAuditLogger> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(
        string eventType,
        string targetType,
        Guid? targetId,
        DevPersonaContext? actor,
        object? payload = null,
        HttpContext? httpContext = null,
        CancellationToken ct = default)
    {
        var doc = payload is null
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(JsonSerializer.Serialize(payload));

        var ev = new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorPersona = actor?.Persona.ToString().ToLowerInvariant() ?? "system",
            ActorDisplay = actor?.DisplayName ?? "system",
            ActorId = null,
            TargetType = targetType,
            TargetId = targetId,
            Payload = doc,
            OccurredAt = DateTimeOffset.UtcNow,
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString(),
        };

        await _db.AuditEvents.AddAsync(ev, ct);
        // Caller is responsible for the SaveChanges, OR we save inline.
        // For Phase B.3.3 simplicity we save inline so loggers can be
        // sprinkled freely without coordinating transactions.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Audit {EventType} target={TargetType}/{TargetId} actor={Actor}",
            eventType, targetType, targetId, ev.ActorDisplay);
    }
}
