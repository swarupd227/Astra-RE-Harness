using System.Net.Sockets;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Parser;
using Astra.Api.Persistence;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Endpoints;

public static class HealthEndpoints
{
    /// <summary>The commit the running image was built from (ASTRA_BUILD_SHA,
    /// stamped by the Dockerfile's BUILD_SHA arg); "dev" outside an image.</summary>
    public static readonly string Build =
        Environment.GetEnvironmentVariable("ASTRA_BUILD_SHA") is { Length: > 0 } sha ? sha : "dev";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: process is up. No I/O.
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "astra-api", build = Build }))
            .WithName("health")
            .ExcludeFromDescription();

        // Readiness: every dependency is reachable.
        app.MapGet("/health/ready", async (
            AppDbContext db,
            IBlobClient blob,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            IFortranParserClient parser,
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

            // Parser sidecar — gRPC Ping so the check reports the build that is
            // actually serving (name + version); falls back to a TCP probe when
            // the RPC fails so a half-up container still shows as reachable.
            checks.Add(await ParserProbe(parser, cfg["Parser:GrpcEndpoint"], ct));

            var allOk = checks.All(c => c.Status == "ok");
            var payload = new
            {
                status = allOk ? "ready" : "degraded",
                service = "astra-api",
                build = Build,
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

    private static async Task<DependencyCheck> ParserProbe(IFortranParserClient parser, string? url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var ping = await parser.PingAsync(cts.Token);
            return new DependencyCheck("parser", "ok", null, $"{ping.Service} {ping.Version}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A port that accepts TCP but cannot answer Ping is a half-up
            // container (stale image, crashed interpreter): report it down,
            // but say which of the two it was.
            var tcp = await TcpProbe("parser", url, ct);
            var reach = tcp.Status == "ok" ? "tcp reachable" : tcp.Error;
            return new DependencyCheck("parser", "down", $"ping failed: {ex.Message} ({reach})");
        }
    }

    private sealed record DependencyCheck(string Name, string Status, string? Error, string? Version = null);
}
