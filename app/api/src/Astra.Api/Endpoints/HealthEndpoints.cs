using System.Net.Sockets;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: process is up. No I/O.
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "astra-api" }))
            .WithName("health")
            .ExcludeFromDescription();

        // Readiness: every dependency is reachable.
        app.MapGet("/health/ready", async (
            AppDbContext db,
            IBlobClient blob,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var checks = new List<DependencyCheck>();

            // Postgres
            try
            {
                var ok = await db.Database.CanConnectAsync(ct);
                checks.Add(new("postgres", ok ? "ok" : "down", null));
            }
            catch (Exception ex)
            {
                checks.Add(new("postgres", "down", ex.Message));
            }

            // MinIO
            try
            {
                var ok = await blob.PingAsync(ct);
                checks.Add(new("minio", ok ? "ok" : "down", null));
            }
            catch (Exception ex)
            {
                checks.Add(new("minio", "down", ex.Message));
            }

            // Parser sidecar — TCP probe (gRPC HTTP/2 setup is heavier; TCP is enough for Phase A)
            checks.Add(await TcpProbe("parser", cfg["Parser:GrpcEndpoint"], ct));

            var allOk = checks.All(c => c.Status == "ok");
            var payload = new
            {
                status = allOk ? "ready" : "degraded",
                service = "astra-api",
                dependencies = checks
            };
            return allOk ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
        }).WithName("readiness");

        return app;
    }

    public static IEndpointRouteBuilder MapWhoamiEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/whoami", (
            DevPersonaContext personaCtx,
            IOptions<DevPersonaOptions> opts,
            ILlmProvider llm) =>
            Results.Ok(new
            {
                persona = personaCtx.Persona.ToString().ToLowerInvariant(),
                displayName = personaCtx.DisplayName,
                isBypass = personaCtx.IsBypass,
                bypassEnabled = opts.Value.DevPersonaBypass,
                defaultPersona = opts.Value.DevPersonaDefault,
                // Phase C.5 prelude: expose the active LLM provider so the
                // chaos test (and any UI provider-banner) doesn't need to
                // consume a real extraction just to find out.
                llmProvider = llm.Info.Name,
                llmModel = llm.Info.Model,
            }));
        return app;
    }

    private static async Task<DependencyCheck> TcpProbe(string name, string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new DependencyCheck(name, "down", "endpoint not configured");

        try
        {
            var uri = new Uri(url);
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return new DependencyCheck(name, tcp.Connected ? "ok" : "down", null);
        }
        catch (Exception ex)
        {
            return new DependencyCheck(name, "down", ex.Message);
        }
    }

    private sealed record DependencyCheck(string Name, string Status, string? Error);
}
