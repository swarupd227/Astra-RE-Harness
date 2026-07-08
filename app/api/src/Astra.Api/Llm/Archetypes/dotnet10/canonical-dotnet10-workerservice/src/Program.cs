using Demo.BatchWorker;
using Demo.BatchWorker.Workers;
using Demo.BatchWorker.Services;
using Demo.BatchWorker.Repositories;
using Demo.BatchWorker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

// [CFG-1] Serilog configured from appsettings.json, replacing Windows Event Log
//         (System.Diagnostics.EventLog) and ConfigurationManager.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var host = Host.CreateDefaultBuilder(args)
    // Enables deployment as a Windows Service (sc.exe create/start/stop).
    // On Linux this becomes a no-op; systemd is configured separately.
    .UseWindowsService(opts => opts.ServiceName = "Demo.BatchWorker")
    .UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext())
    .ConfigureServices((ctx, services) =>
    {
        // [DI-2] DbContext registered as transient via factory so Singleton-
        //        scoped consumers (BackgroundService) can safely create short-
        //        lived contexts per batch run without DbContext concurrency issues.
        services.AddDbContextFactory<BatchDbContext>(opt =>
            opt.UseSqlServer(ctx.Configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException("ConnectionStrings:Default missing")));

        // [DI-1] Domain services — registered as Transient to match the
        //        per-batch lifetime of each IDbContextFactory-created context.
        services.AddTransient<IBatchRepository, BatchRepository>();
        services.AddTransient<BatchService>();

        // [OA-1] PeriodicTimer-based worker replaces ServiceBase + Thread.Sleep.
        services.AddHostedService<BatchProcessorWorker>();
    })
    .Build();

await host.RunAsync();
