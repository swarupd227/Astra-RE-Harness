using Astra.Worker.Jobs;
using Hangfire;
using Hangfire.PostgreSql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ─── Logging ──────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "astra-worker")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

// ─── Hangfire ─────────────────────────────────────────────────────────
var pgConn = builder.Configuration.GetConnectionString("Postgres")
             ?? throw new InvalidOperationException("Postgres connection string missing.");

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(pgConn)));
builder.Services.AddHangfireServer(o =>
{
    o.WorkerCount = Math.Max(2, Environment.ProcessorCount);
    o.ServerName = $"astra-worker:{Environment.MachineName}";
});

// Make jobs DI-resolvable
builder.Services.AddScoped<PingJob>();

// ─── OpenTelemetry ────────────────────────────────────────────────────
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "astra-worker";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: serviceName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.UseSerilogRequestLogging();

// Liveness for compose healthcheck
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "astra-worker" }));

// Hangfire dashboard — Phase A: open in dev. Locked down once real auth ships.
if (builder.Configuration.GetValue("Hangfire:DashboardEnabled", true))
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new AllowAllDashboardAuth() },
        DisplayStorageConnectionString = false,
        IgnoreAntiforgeryToken = true
    });
}

// Schedule the heartbeat job
if (builder.Configuration.GetValue("Hangfire:SchedulePingJob", true))
{
    var recurring = app.Services.GetRequiredService<IRecurringJobManager>();
    recurring.AddOrUpdate<PingJob>(
        "ping",
        job => job.ExecuteAsync(),
        Cron.Minutely);
    Log.Information("Scheduled recurring PingJob (every minute)");
}

await app.RunAsync();

/// <summary>
///     Phase A: dashboard is open within the Docker network.
///     Replace with persona-aware policy once OIDC ships.
/// </summary>
internal sealed class AllowAllDashboardAuth : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context) => true;
}
