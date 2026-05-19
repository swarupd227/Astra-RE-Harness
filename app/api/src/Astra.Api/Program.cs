using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Endpoints;
using Astra.Api.Ingest;
using Astra.Api.Llm;
using Astra.Api.Parser;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Seed;
using Astra.Api.Signing;
using Astra.Api.Storage;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Minio;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

// ─── Bootstrap logger ──────────────────────────────────────────────────
// Captures startup-time messages (e.g. provider-fallback warnings) that fire
// during service registration, before the host's UseSerilog pipeline is wired.
// Without this, Serilog's default static logger is silent and warnings vanish.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "astra-api")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ─── Logging (Serilog) ────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "astra-api")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

// ─── Persistence (Postgres via EF Core) ───────────────────────────────
var pgConn = builder.Configuration.GetConnectionString("Postgres")
             ?? throw new InvalidOperationException("Postgres connection string missing.");
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(pgConn, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history")));

// ─── MediatR ──────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// ─── FluentValidation ─────────────────────────────────────────────────
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// ─── MinIO blob storage ───────────────────────────────────────────────
builder.Services.AddSingleton<StorageOptions>(sp =>
    builder.Configuration.GetSection("Storage").Get<StorageOptions>()
        ?? throw new InvalidOperationException("Storage section missing."));

builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var opts = sp.GetRequiredService<StorageOptions>();
    var endpointUri = new Uri(opts.Endpoint);
    return new MinioClient()
        .WithEndpoint(endpointUri.Host, endpointUri.Port)
        .WithCredentials(opts.AccessKey, opts.SecretKey)
        .WithSSL(endpointUri.Scheme == "https")
        .Build();
});
builder.Services.AddSingleton<IBlobClient, MinioBlobClient>();

// ─── Dev-persona auth shim (Phase A; OIDC replaces this in Phase C) ───
builder.Services.Configure<DevPersonaOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddScoped<DevPersonaContext>();

// ─── Seed pipeline ────────────────────────────────────────────────────
builder.Services.AddScoped<ConsumeRollSeed>();
builder.Services.AddScoped<MinpackDemoSeed>();
builder.Services.AddScoped<GoldenDatasetSeed>();

// ─── LLM provider + extraction pipeline ──────────────────────────────
// Selectable via Llm:Provider — mock (default, offline), fail-mock (chaos),
// anthropic (real Claude). Anthropic falls back to mock with a warning if
// the API key is not configured, so a fresh-clone run is never broken by
// missing secrets.
var llmProvider = (builder.Configuration.GetValue("Llm:Provider", "mock") ?? "mock").ToLowerInvariant();
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Llm:Anthropic"));
switch (llmProvider)
{
    case "mock":
        builder.Services.AddSingleton<ILlmProvider, MockLlmProvider>();
        break;
    case "fail-mock":
        builder.Services.AddSingleton<ILlmProvider, FailMockLlmProvider>();
        break;
    case "anthropic":
    {
        var anthropicKey = builder.Configuration.GetValue<string>("Llm:Anthropic:ApiKey");
        if (string.IsNullOrWhiteSpace(anthropicKey))
        {
            Log.Warning(
                "Llm:Provider=anthropic but Llm:Anthropic:ApiKey is empty. " +
                "Falling back to MockLlmProvider so the demo flow still works. " +
                "Set ANTHROPIC_API_KEY in .env to enable the real provider.");
            builder.Services.AddSingleton<ILlmProvider, MockLlmProvider>();
        }
        else
        {
            builder.Services.AddHttpClient<AnthropicLlmProvider>();
            builder.Services.AddSingleton<ILlmProvider>(sp =>
                sp.GetRequiredService<AnthropicLlmProvider>());
        }
        break;
    }
    default:
        throw new InvalidOperationException(
            $"Unknown Llm:Provider '{llmProvider}'. Valid values: mock, fail-mock, anthropic.");
}
// Phase 7.0 — structured cross-routine context builder. Used by
// ExtractionPipeline to attach a neighbourhood to every ExtractionRequest.
builder.Services.AddScoped<Astra.Api.Llm.NeighbourhoodBuilder>();
builder.Services.AddScoped<ExtractionPipeline>();

// Phase 7.1 — Cross-routine harmonisation pipeline. Loads every signed
// spec in a corpus, sends to the LLM in one call, persists structured
// findings the SME can accept/dismiss.
builder.Services.AddHttpClient("anthropic-harmonise");
builder.Services.AddScoped<Astra.Api.Llm.HarmonisationPipeline>();

// ─── Stage-5 scaffold provider + pipeline (Phase B.4) ────────────────
var scaffoldProvider = builder.Configuration.GetValue("Llm:ScaffoldProvider", "mock") ?? "mock";
switch (scaffoldProvider.ToLowerInvariant())
{
    case "mock":
        builder.Services.AddSingleton<IScaffoldProvider, MockScaffoldProvider>();
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown Llm:ScaffoldProvider '{scaffoldProvider}'. Valid values for Phase B.4: mock.");
}
builder.Services.AddScoped<ScaffoldPipeline>();

// ─── Externalised spec schemas (Phase #3a) — singleton, loaded at startup
builder.Services.AddSingleton<Astra.Api.Llm.Schemas.SpecSchemaProvider>();

// ─── Externalised prompt asset library (Phase #3b) — singleton, loaded at startup
builder.Services.AddSingleton<Astra.Api.Llm.Prompts.PromptLibrary>();

// ─── Externalised scaffold archetype registry (Phase #3c) — singleton, loaded at startup
builder.Services.AddSingleton<Astra.Api.Llm.Archetypes.ArchetypeRegistry>();

// ─── Compliance feed exporter (Phase #3d) — scoped, joins audit + signature
builder.Services.AddScoped<Astra.Api.Compliance.ComplianceFeedExporter>();

// ─── Post-migration validation (Phase #2a/2b/2c) ──────────────────────
builder.Services.AddScoped<Astra.Api.Validation.CompileValidator>();
builder.Services.AddScoped<Astra.Api.Validation.TestPackGenerator>();
builder.Services.AddScoped<Astra.Api.Validation.TestPackValidator>();
builder.Services.AddScoped<Astra.Api.Validation.GoldenDatasetScorer>();
builder.Services.AddHttpClient("gfortran");
builder.Services.AddScoped<Astra.Api.Validation.GfortranClient>();
builder.Services.AddScoped<Astra.Api.Validation.CrossRuntimeValidator>();

// ─── Maven sidecar HTTP client (Phase 5.5) ────────────────────────────
// Mirrors the gfortran wiring above so the validator path can dispatch
// java-spring scaffolds through `mvn compile` + JUnit instead of dotnet.
builder.Services.AddHttpClient("maven");
builder.Services.AddScoped<Astra.Api.Validation.MavenClient>();

// ─── GnuCOBOL sidecar HTTP client (Phase 5.6) ────────────────────────
// Same shape as the gfortran wiring above; drives the COBOL reference
// binary for the per-routine equivalence harness (DEPTPAY's
// AVERAGE-SALARY paragraph in Phase 5.6, more programs in Phase 5.7).
builder.Services.AddHttpClient("gnucobol");
builder.Services.AddScoped<Astra.Api.Validation.GnuCobolClient>();

// ─── Software HSM signer (Phase B.3; Azure Key Vault Managed HSM in Phase D) ─
builder.Services.AddSingleton<IHsmSigner, SoftwareHsmSigner>();

// ─── Audit logger (Phase B.3.3) ───────────────────────────────────────
builder.Services.AddScoped<IAuditLogger, PostgresAuditLogger>();

// ─── HTTP client for parser sidecar health checks ─────────────────────
builder.Services.AddHttpClient("parser");

// ─── Fortran parser gRPC client (Phase C.2) ──────────────────────────
// Singleton so the underlying GrpcChannel is reused across all requests.
builder.Services.AddSingleton<IFortranParserClient, FortranParserClient>();

// ─── Ingest pipeline (Phase C.1) ──────────────────────────────────────
builder.Services.AddScoped<IngestPipeline>();

// ─── OpenTelemetry ────────────────────────────────────────────────────
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "astra-api";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: serviceName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

// ─── CORS for the frontend dev server ─────────────────────────────────
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:35173" };
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// ─── Schema bootstrap + seed ─────────────────────────────────────────
// Phase B: dev iterates fast. We drop & recreate the public schema (safe
// because Hangfire lives in its own schema) and apply the model DDL via
// Database.GenerateCreateScript(). This sidesteps EnsureCreatedAsync's
// "do nothing if the DB exists" semantic.
// Phase D switches to EF Core migrations (Database.MigrateAsync) before
// production cutover.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var canConnect = await db.Database.CanConnectAsync();
    Log.Information("Postgres connectivity: {CanConnect}", canConnect);

    var recreate = builder.Configuration.GetValue("Database:RecreateOnStartup", false);
    var seedDemo = builder.Configuration.GetValue("Database:SeedDemo", true);
    var seedMinpack = builder.Configuration.GetValue("Database:SeedMinpack", false);

    if (canConnect && recreate)
    {
        await db.Database.ExecuteSqlRawAsync("""
            DROP SCHEMA IF EXISTS public CASCADE;
            CREATE SCHEMA public;
            GRANT ALL ON SCHEMA public TO PUBLIC;
            CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
            CREATE EXTENSION IF NOT EXISTS "pg_trgm";
            """);
        var script = db.Database.GenerateCreateScript();
        await db.Database.ExecuteSqlRawAsync(script);
        Log.Information("Schema rebuilt from model ({Bytes} bytes of DDL)", script.Length);
    }

    // Phase #4 additive schema — small CREATE TABLE IF NOT EXISTS statements
    // for Admin-CRUD surfaces so existing dev databases pick them up without
    // a full RecreateOnStartup cycle (which would wipe demo state).
    // Phase D replaces these with proper EF migrations.
    if (canConnect)
    {
        // ExecuteSqlRawAsync's "{}" parameter-substitution syntax conflicts
        // with the jsonb literal `'{}'::jsonb` and with `varchar(160)
        // DEFAULT ''`, so the column defaults are omitted here — every
        // insert sets these columns explicitly.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS platform_configs (
                key                varchar(64) PRIMARY KEY,
                value_json         jsonb       NOT NULL,
                updated_at         timestamptz NOT NULL,
                updated_by         uuid        NULL,
                updated_by_display varchar(160) NOT NULL
            );
            """);

        // Phase 5.2 — per-Subroutine source-language column. Default keeps
        // existing rows valid as fortran-f77; future inserts set it
        // explicitly per-file. Idempotent.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE subroutines
              ADD COLUMN IF NOT EXISTS source_language varchar(32) NOT NULL DEFAULT 'fortran-f77';
            CREATE INDEX IF NOT EXISTS ix_subroutines_source_language
              ON subroutines (source_language);
            """);

        // Phase 6.0 — Golden dataset tables. Additive create-if-not-exists
        // so existing dev databases pick them up without a full recreate.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS golden_dataset_entries (
                id              uuid        PRIMARY KEY,
                entry_id        varchar(128) NOT NULL,
                schema_id       varchar(32)  NOT NULL,
                title           varchar(256) NOT NULL,
                trap_category   varchar(64)  NOT NULL,
                difficulty      varchar(16)  NOT NULL,
                source_path     varchar(512) NOT NULL,
                source_content  text         NOT NULL,
                source_lines    varchar(32)  NOT NULL,
                expected_claims jsonb        NOT NULL,
                canonical_inputs jsonb       NOT NULL,
                notes           text         NOT NULL,
                status          varchar(32)  NOT NULL,
                created_at      timestamptz  NOT NULL,
                updated_at      timestamptz  NOT NULL,
                updated_by      varchar(160) NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_golden_dataset_entries_entry_id
              ON golden_dataset_entries (entry_id);
            CREATE INDEX IF NOT EXISTS ix_golden_dataset_entries_schema_status
              ON golden_dataset_entries (schema_id, status);
            CREATE INDEX IF NOT EXISTS ix_golden_dataset_entries_trap_category
              ON golden_dataset_entries (trap_category);

            CREATE TABLE IF NOT EXISTS golden_dataset_runs (
                id              uuid        PRIMARY KEY,
                entry_id        uuid        NOT NULL REFERENCES golden_dataset_entries(id) ON DELETE CASCADE,
                llm_call_id     uuid        NULL,
                prompt_id       varchar(128) NOT NULL,
                prompt_version  varchar(32)  NOT NULL,
                model_name      varchar(128) NOT NULL,
                matched         integer      NOT NULL,
                total           integer      NOT NULL,
                score           double precision NOT NULL,
                detail          jsonb        NOT NULL,
                started_at      timestamptz  NOT NULL,
                completed_at    timestamptz  NOT NULL,
                triggered_by    varchar(160) NULL
            );
            CREATE INDEX IF NOT EXISTS ix_golden_dataset_runs_prompt
              ON golden_dataset_runs (prompt_id, prompt_version, completed_at);
            CREATE INDEX IF NOT EXISTS ix_golden_dataset_runs_entry
              ON golden_dataset_runs (entry_id, completed_at);
            """);

        // Phase 7.1 — Cross-routine harmonisation. Additive
        // create-if-not-exists; existing dev databases pick these up
        // without a full recreate.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS harmonisation_runs (
                id                      uuid        PRIMARY KEY,
                corpus_id               uuid        NOT NULL,
                source_version_id       uuid        NOT NULL,
                status                  varchar(32) NOT NULL,
                prompt_id               varchar(128) NOT NULL,
                prompt_version          varchar(32)  NOT NULL,
                model_name              varchar(128) NOT NULL,
                input_tokens            integer      NOT NULL,
                output_tokens           integer      NOT NULL,
                cache_read_tokens       integer      NOT NULL,
                cache_creation_tokens   integer      NOT NULL,
                spec_count              integer      NOT NULL,
                finding_count           integer      NOT NULL,
                summary                 text         NOT NULL,
                error_message           text         NULL,
                triggered_by            varchar(160) NULL,
                started_at              timestamptz  NOT NULL,
                completed_at            timestamptz  NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_harmonisation_runs_corpus
              ON harmonisation_runs (corpus_id, completed_at);

            CREATE TABLE IF NOT EXISTS harmonisation_findings (
                id                      uuid        PRIMARY KEY,
                harmonisation_run_id    uuid        NOT NULL REFERENCES harmonisation_runs(id) ON DELETE CASCADE,
                category                varchar(64) NOT NULL,
                severity                varchar(16) NOT NULL,
                title                   varchar(256) NOT NULL,
                detail                  text         NOT NULL,
                affected_spec_ids       jsonb        NOT NULL,
                status                  varchar(16)  NOT NULL,
                admin_note              text         NULL,
                created_at              timestamptz  NOT NULL,
                updated_at              timestamptz  NOT NULL,
                updated_by              varchar(160) NULL
            );
            CREATE INDEX IF NOT EXISTS ix_harmonisation_findings_run_severity
              ON harmonisation_findings (harmonisation_run_id, severity);
            CREATE INDEX IF NOT EXISTS ix_harmonisation_findings_run_status
              ON harmonisation_findings (harmonisation_run_id, status);
            """);
    }
    if (canConnect && seedDemo)
    {
        var seeder = scope.ServiceProvider.GetRequiredService<ConsumeRollSeed>();
        await seeder.SeedAsync();
    }
    // Phase 6.0 — Golden dataset is seeded unconditionally (small payload,
    // idempotent, exists independently of the corpus-demo seed).
    if (canConnect)
    {
        var goldenSeeder = scope.ServiceProvider.GetRequiredService<GoldenDatasetSeed>();
        try { await goldenSeeder.SeedAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Golden dataset seed failed"); }
    }
    if (canConnect && seedMinpack)
    {
        // Fire-and-forget so the API can start serving even while the
        // ~10-second clone is still running. The seed is idempotent;
        // refreshing the browser after it lands surfaces the corpus.
        //
        // Background tasks MUST create their own DI scope — the startup
        // scope (this `using`) disposes before the task runs.
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = scopeFactory.CreateScope();
                var seeder = bgScope.ServiceProvider.GetRequiredService<MinpackDemoSeed>();
                await seeder.SeedAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Background MINPACK seed failed"); }
        });
    }
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseMiddleware<DevPersonaMiddleware>();

app.MapHealthEndpoints();
app.MapWhoamiEndpoint();
app.MapSystemStatsEndpoint();
app.MapCorpusEndpoints();
app.MapIngestEndpoints();
app.MapSubroutineEndpoints();
app.MapExtractionEndpoints();
app.MapSpecReviewEndpoints();
app.MapAuditEndpoints();
app.MapMyReviewsEndpoints();
app.MapScaffoldEndpoints();
app.MapValidationEndpoints();
app.MapSchemaEndpoints();
app.MapPromptEndpoints();
app.MapArchetypeEndpoints();
app.MapGoldenDatasetEndpoints();
app.MapHarmonisationEndpoints();
app.MapComplianceEndpoints();
app.MapProviderEndpoints();
app.MapRolesEndpoints();
app.MapValidationPolicyEndpoints();
app.MapSignatureHealthEndpoints();
app.MapCommentEndpoints();
app.MapNotificationEndpoints();
app.MapEvidenceEndpoints();
app.MapDevEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    service = "astra-api",
    version = "0.1.0",
    docs = "/health, /api/v1/whoami"
}));

await app.RunAsync();

// Make Program reachable from tests later
public partial class Program;
