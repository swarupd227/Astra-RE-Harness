using Astra.Api.Auth;

namespace Astra.Api.Audit;

/// <summary>
/// Append-only audit emission. Phase B.3.3 ships a Postgres-backed implementation;
/// the interface stays stable so SOC 2 / ISO 27001 collectors can swap a
/// fan-out to S3-compatible cold storage in Phase D without touching callers.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
        string eventType,
        string targetType,
        Guid? targetId,
        DevPersonaContext? actor,
        object? payload = null,
        HttpContext? httpContext = null,
        CancellationToken ct = default);
}
