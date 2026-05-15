using Astra.Api.Persistence;
using Astra.Api.Persistence.Seed;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Dev-only endpoints for E2E and manual reset of demo state.
/// Gated by <c>Dev:ResetEnabled</c> in configuration; the docker-compose
/// stack defaults this to true and any deployed environment must leave it
/// off (false).
/// </summary>
public static class DevEndpoints
{
    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/dev/reset", async (
            AppDbContext db,
            ConsumeRollSeed seed,
            IConfiguration cfg,
            IServiceScopeFactory scopeFactory,
            ILogger<DevEndpointMarker> log,
            CancellationToken ct) =>
        {
            if (!cfg.GetValue("Dev:ResetEnabled", false))
                return Results.NotFound(new { error = new { code = "dev.reset_disabled" } });

            // Drop & recreate the public schema, then re-seed.  Hangfire lives
            // in its own schema (`hangfire`) so it survives.
            await db.Database.ExecuteSqlRawAsync("""
                DROP SCHEMA IF EXISTS public CASCADE;
                CREATE SCHEMA public;
                GRANT ALL ON SCHEMA public TO PUBLIC;
                CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
                CREATE EXTENSION IF NOT EXISTS "pg_trgm";
                """, ct);
            var script = db.Database.GenerateCreateScript();
            await db.Database.ExecuteSqlRawAsync(script, ct);
            await seed.SeedAsync(ct);

            // MINPACK demo seed runs in the background — it takes ~10s to
            // clone + parse and we don't want /dev/reset to block on it.
            // The HTTP response surfaces `minpackSeeding=true` so callers
            // (e2e tests, demo scripts) know to wait for it if they need it.
            //
            // The background task creates its OWN DI scope — the request
            // scope (the one this handler ran under) disposes when the
            // response returns, so the captured `MinpackDemoSeed` would
            // be dead by the time Task.Run actually executed.
            var seedMinpack = cfg.GetValue("Database:SeedMinpack", false);
            if (seedMinpack)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var bgScope = scopeFactory.CreateScope();
                        var seeder = bgScope.ServiceProvider.GetRequiredService<MinpackDemoSeed>();
                        await seeder.SeedAsync();
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Background MINPACK reseed failed"); }
                });
            }

            log.LogWarning("Demo state reset via POST /api/v1/dev/reset");
            return Results.Ok(new { reset = true, seeded = true, minpackSeeding = seedMinpack });
        });

        return app;
    }

    private sealed class DevEndpointMarker;
}
