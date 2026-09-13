using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Docs;
using Astra.Api.Endpoints;
using Astra.Api.Ingest;
using Astra.Api.Llm;
using Astra.Api.Parser;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Seed;
using Astra.Api.Signing;
using Astra.Api.Storage;
using Azure.Storage.Blobs;
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

// ─── Request body limits ──────────────────────────────────────────────
// The ingest upload cap (IngestEndpoints.MaxUploadBytes, 600 MiB) is the
// business rule; these two transport-layer limits must sit ABOVE it or
// they reject big uploads with a bare 413/400 before the endpoint can
// return its friendly ingest.upload_too_large error. 640 MiB leaves
// headroom for multipart boundary/header overhead.
const long RequestBodyCeiling = 640L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = RequestBodyCeiling);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = RequestBodyCeiling;
});

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

// ─── Blob storage: MinIO (default, local dev) or native Azure Blob
// Storage (Storage__Provider=azureblob — added after MinIO-on-Azure-Files
// proved unreliable; see AzureBlobStorageClient's doc comment). ─────────
builder.Services.AddSingleton<StorageOptions>(sp =>
    builder.Configuration.GetSection("Storage").Get<StorageOptions>()
        ?? throw new InvalidOperationException("Storage section missing."));

var storageProvider = builder.Configuration["Storage:Provider"] ?? "minio";
if (string.Equals(storageProvider, "azureblob", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(sp =>
        new BlobServiceClient(sp.GetRequiredService<StorageOptions>().AzureBlobConnectionString));
    builder.Services.AddSingleton<IBlobClient, AzureBlobStorageClient>();
}
else
{
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
}

// ─── Dev-persona auth shim (Phase A; OIDC replaces this in Phase C) ───
builder.Services.Configure<DevPersonaOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddScoped<DevPersonaContext>();

// ─── Seed pipeline ────────────────────────────────────────────────────
builder.Services.AddScoped<ConsumeRollSeed>();
builder.Services.AddScoped<MinpackDemoSeed>();
builder.Services.AddScoped<LapackBlasSeed>();
builder.Services.AddScoped<IndyDemoSeed>();
builder.Services.AddScoped<FmtDemoSeed>();
builder.Services.AddScoped<Vb6DemoSeed>();
builder.Services.AddScoped<MvcMusicStoreSeed>();
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
// Phase 15.2 — survey-digest tier for pattern analysis (Haiku by default,
// bound from Llm:Survey). Follows the provider choice above: real Claude
// when Llm:Provider=anthropic with a key, otherwise the deterministic mock
// so the survey → cluster pipeline still runs offline.
builder.Services.Configure<Astra.Api.Llm.PatternAnalysis.SurveyOptions>(builder.Configuration.GetSection("Llm:Survey"));
builder.Services.AddHttpClient("anthropic-survey", c => c.Timeout = TimeSpan.FromMinutes(3));
if (llmProvider == "anthropic" && !string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("Llm:Anthropic:ApiKey")))
    builder.Services.AddSingleton<Astra.Api.Llm.PatternAnalysis.ISurveyProvider, Astra.Api.Llm.PatternAnalysis.AnthropicSurveyProvider>();
else
    builder.Services.AddSingleton<Astra.Api.Llm.PatternAnalysis.ISurveyProvider, Astra.Api.Llm.PatternAnalysis.MockSurveyProvider>();
builder.Services.AddSingleton<Astra.Api.Llm.PatternAnalysis.SurveyStage>();
// Resumes RESUMABLE pattern-analysis runs after boot (Llm:PatternAnalysis:AutoResume, default true).
builder.Services.AddHostedService<Astra.Api.Llm.PatternAnalysis.PatternAnalysisResumeService>();

// WS2 — the conversation spine + Astra orchestrator. Threads/messages are
// Scoped (own AppDbContext); the orchestrator loop runs inside the SSE
// request so it is Scoped too and calls the same Scoped pipelines the REST
// endpoints use. Background runs (extract/scaffold/gate) and the Narrator
// are singletons that create their own scope per unit of work, copying the
// requesting persona in. The model behind the loop is Anthropic when a key
// is configured, else a deterministic keyword router so the Workspace works
// offline and in e2e.
builder.Services.Configure<Astra.Api.Copilot.CopilotOptions>(builder.Configuration.GetSection("Llm:Copilot"));
builder.Services.AddHttpClient("anthropic-copilot", c => c.Timeout = TimeSpan.FromMinutes(5));
if (llmProvider == "anthropic" && !string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("Llm:Anthropic:ApiKey")))
    builder.Services.AddSingleton<Astra.Api.Copilot.ICopilotBrain, Astra.Api.Copilot.AnthropicCopilotBrain>();
else
    builder.Services.AddSingleton<Astra.Api.Copilot.ICopilotBrain, Astra.Api.Copilot.MockCopilotBrain>();
builder.Services.AddScoped<Astra.Api.Conversations.ConversationService>();
builder.Services.AddScoped<Astra.Api.Specs.SpecReviewService>();
builder.Services.AddScoped<Astra.Api.Copilot.CopilotToolRegistry>();
builder.Services.AddScoped<Astra.Api.Copilot.CopilotOrchestrator>();
builder.Services.AddSingleton<Astra.Api.Copilot.BackgroundRunService>();
builder.Services.AddSingleton<Astra.Api.Copilot.Narrator>();
// WS5 — the 10-minute Assessment (deterministic facts + one narrative call).
builder.Services.AddSingleton<Astra.Api.Assessment.AssessmentService>();

// Task #178 — runtime LLM key management. Remember the boot-time key so a
// database override can be reverted, and make sure the plain HttpClient
// factory exists even when the provider booted in mock fallback (the
// settings test endpoint needs it regardless).
builder.Services.AddSingleton(new LlmKeyState(
    builder.Configuration.GetValue<string>("Llm:Anthropic:ApiKey") ?? ""));
builder.Services.AddHttpClient();
// Phase 15.2 — process-wide adaptive concurrency gate for every Anthropic
// call. Honours Retry-After / anthropic-ratelimit-* headers so bulk passes
// can run at 32-wide without turning a rate-limit wall into wasted
// generations. Bound from Llm:Anthropic:RateLimit.
builder.Services.Configure<Astra.Api.Llm.AnthropicRateLimiterOptions>(
    builder.Configuration.GetSection("Llm:Anthropic:RateLimit"));
builder.Services.AddSingleton<Astra.Api.Llm.AnthropicRateLimiter>();
// Phase 15.2 — structured, multi-subscriber run-event bus (ring buffer +
// fan-out). DocRunLogger is now a string façade over it, so the existing
// docs SSE route keeps working while new surfaces read typed events.
builder.Services.AddSingleton<Astra.Api.Runs.RunEventBus>();
// Phase 7.0 — structured cross-routine context builder. Used by
// ExtractionPipeline to attach a neighbourhood to every ExtractionRequest.
// Backs the per-source-version routine index the neighbourhood
// builder reuses across a bulk extraction pass.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Astra.Api.Llm.NeighbourhoodBuilder>();
builder.Services.AddScoped<ExtractionPipeline>();

// Phase 7.1 — Cross-routine harmonisation pipeline. Loads every signed
// spec in a corpus, sends to the LLM in one call, persists structured
// findings the SME can accept/dismiss.
builder.Services.AddHttpClient("anthropic-harmonise");
builder.Services.AddScoped<Astra.Api.Llm.HarmonisationPipeline>();

// Phase 8.0.a — Dependency graph builder. Pure derived state from
// the parser-extracted call-graph + COMMON-block metadata already on
// every Subroutine row. Substrate for the Phase 8.0.b wave planner +
// 8.0.c blast-radius services.
builder.Services.AddScoped<Astra.Api.Llm.Dependency.DependencyGraphBuilder>();

// Phase 8.0.b — Migration planner. Topological wave assignment over
// the dependency graph; persists MigrationPlan + MigrationWave rows.
builder.Services.AddScoped<Astra.Api.Llm.Dependency.MigrationPlanner>();

// Phase 11.0 vertical slice — documentation generation service. Uses
// a named HttpClient so the timeout setting in DocsExtractionService
// composes cleanly with the global HTTP infra.
builder.Services.AddHttpClient("docs-summary");
builder.Services.AddScoped<Astra.Api.Docs.DocsExtractionService>();

// Phase 11.0.a — production routine-summary pipeline.
// Bind Docs:Generator options, register the tier classifier + pipeline.
// The pipeline backgrounds via Task.Run + IServiceScopeFactory because
// the request scope dies the moment the endpoint returns 202.
builder.Services.Configure<Astra.Api.Docs.DocsOptions>(builder.Configuration.GetSection("Docs:Generator"));
builder.Services.AddScoped<Astra.Api.Docs.RoutineTierClassifier>();
builder.Services.AddSingleton<Astra.Api.Docs.RoutineSummaryPipeline>();
// Phase 11.0.b — module + overview rollup; orchestrator chains stages.
builder.Services.AddSingleton<Astra.Api.Docs.HierarchicalRollupPipeline>();
// Phase 11.0.c — cross-cutting catalogs.
builder.Services.AddSingleton<Astra.Api.Docs.CatalogPipeline>();
// Phase 11.0.d — sequence + dependency diagrams.
builder.Services.AddSingleton<Astra.Api.Docs.DiagramPipeline>();
builder.Services.AddSingleton<Astra.Api.Docs.DocsGenerationOrchestrator>();
// WS6 — model-authored prose, style guide + exemplars, critic/revise pass
// (IDocWriter, DocPromptAssets, DocCriticPass; see Docs/DocsQualityRegistration.cs).
Astra.Api.Docs.DocsQualityRegistration.AddDocsQuality(builder.Services);
// Phase 11.0.f — in-process SSE log bus for generation runs.
builder.Services.AddSingleton<Astra.Api.Docs.DocRunLogger>();

// Phase 12.0 — Pattern analysis (bulk extraction + claim-kind clustering).
// Singleton orchestrator resolves ExtractionPipeline (Scoped) and AppDbContext
// per work item via IServiceScopeFactory, same pattern as the Docs pipelines
// above — the request scope dies the moment the trigger endpoint returns 202.
builder.Services.AddSingleton<Astra.Api.Llm.PatternAnalysis.PatternAnalysisOrchestrator>();

// Phase 14.0 — Live archetype authoring. Scoped (uses AppDbContext + MavenClient
// directly, called synchronously from the propose endpoint — a single LLM
// call plus one compile+test pass, same latency class as HarmonisationPipeline).
builder.Services.AddHttpClient("anthropic-propose-archetype");
// The whole-corpus clustering call generates a large structured response;
// big corpora ran past the default HttpClient timeout (EnvestNet hit the
// 600s cap mid-generation), so give this one explicit headroom.
builder.Services.AddHttpClient("anthropic-cluster-patterns", c => c.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddScoped<Astra.Api.Llm.PatternAnalysis.ArchetypeAuthoringService>();

// Phase 8.0.e — Pluggable migration strategies. Each implementation
// is registered as IPlanStrategy; MigrationPlanner picks one by name
// at plan-generation time. Order doesn't matter — name lookup keys
// the dispatch. Customers can register additional IPlanStrategy
// implementations alongside these built-ins.
builder.Services.AddSingleton<Astra.Api.Llm.Dependency.IPlanStrategy, Astra.Api.Llm.Dependency.Strategies.TopologicalLeavesFirstStrategy>();
builder.Services.AddSingleton<Astra.Api.Llm.Dependency.IPlanStrategy, Astra.Api.Llm.Dependency.Strategies.BusinessPriorityStrategy>();
builder.Services.AddSingleton<Astra.Api.Llm.Dependency.IPlanStrategy, Astra.Api.Llm.Dependency.Strategies.RiskFirstStrategy>();
builder.Services.AddSingleton<Astra.Api.Llm.Dependency.IPlanStrategy, Astra.Api.Llm.Dependency.Strategies.PilotThenScaleStrategy>();

// Phase 8.0.c — Per-routine migration context: blast radius +
// readiness classification + wave assignment lookup. Reads only;
// no schema additions.
builder.Services.AddScoped<Astra.Api.Llm.Dependency.MigrationContextService>();

// Phase 8.0.d — Cross-corpus portfolio metrics. Reads + aggregates
// every corpus + plan + LLM call + audit event into one summary
// payload for the admin dashboard.
builder.Services.AddScoped<Astra.Api.Llm.Dependency.PortfolioMetricsService>();

// ─── Stage-5 scaffold provider + pipeline ────────────────────────────
// Phase 13.0 — "anthropic" now does REAL per-routine customization
// (previously the only implementation, MockScaffoldProvider, streamed a
// matched archetype's static files unchanged regardless of which routine
// triggered it). Same fallback posture as Llm:Provider above: missing key
// falls back to mock with a warning rather than breaking a fresh clone.
var scaffoldProvider = builder.Configuration.GetValue("Llm:ScaffoldProvider", "mock") ?? "mock";
switch (scaffoldProvider.ToLowerInvariant())
{
    case "mock":
        builder.Services.AddSingleton<IScaffoldProvider, MockScaffoldProvider>();
        break;
    case "anthropic":
    {
        var scaffoldKey = builder.Configuration.GetValue<string>("Llm:Anthropic:ApiKey");
        if (string.IsNullOrWhiteSpace(scaffoldKey))
        {
            Log.Warning(
                "Llm:ScaffoldProvider=anthropic but Llm:Anthropic:ApiKey is empty. " +
                "Falling back to MockScaffoldProvider. Set ANTHROPIC_API_KEY in .env to enable real scaffold generation.");
            builder.Services.AddSingleton<IScaffoldProvider, MockScaffoldProvider>();
        }
        else
        {
            builder.Services.AddHttpClient("anthropic-scaffold-generate");
            builder.Services.AddSingleton<IScaffoldProvider, AnthropicScaffoldProvider>();
        }
        break;
    }
    default:
        throw new InvalidOperationException(
            $"Unknown Llm:ScaffoldProvider '{scaffoldProvider}'. Valid values: mock, anthropic.");
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

// ─── VB6 sidecar (Phase 10.0.f / 10.3.c — Wine + cscript reference) ──
// VB6 specs route through this sidecar instead of the default gfortran
// path. Dev tier uses VBScript (.vbs) via Wine + cscript so the dev
// environment doesn't need a licensed VB6 runtime; production tier
// uses native vb6.exe + msvbvm60.dll on Windows Server Core.
builder.Services.AddHttpClient("vb6");
builder.Services.AddScoped<Astra.Api.Validation.Vb6Client>();

// ─── C# / .NET 10 sidecar (Phase 12.0.f) ────────────────────────────
// Compiles and runs .NET 10 C# sources via `dotnet publish`. Routes
// subroutines with SourceLanguage == "csharp" or "vbnet" (VB.NET-sourced
// specs whose scaffolds target dotnet10) to this sidecar rather than
// the gfortran smoke path.
builder.Services.AddHttpClient("csharp");
builder.Services.AddScoped<Astra.Api.Validation.CsharpClient>();

// ─── fpc / gpp sidecars (Phase 15.1.b/c) ─────────────────────────────
// Both containers have run healthy since Phase 9.0.f/9.1.f but had no
// consuming client at all — CrossRuntimeValidator fell through to the
// gfortran smoke test for delphi/cpp sources regardless, reporting a
// fake PASSED. Same shape as every other sidecar client above.
builder.Services.AddHttpClient("fpc");
builder.Services.AddScoped<Astra.Api.Validation.FpcClient>();
builder.Services.AddHttpClient("gpp");
builder.Services.AddScoped<Astra.Api.Validation.GppClient>();

// ─── Property-test sidecar (Phase 9.3.b — 4th validation gate) ───────
// Drives Hypothesis-driven falsifying-input search per ADR-029, per-claim
// generator hints embedded on the signed spec per ADR-030. v1 ships in
// "shadow mode" — the callback acknowledges each generated input without
// running ref vs candidate binary comparison; v1.1 wires the real
// comparison behind the same callback contract.
builder.Services.AddHttpClient("property-test");
builder.Services.AddScoped<Astra.Api.Validation.PropertyTestClient>();
builder.Services.AddScoped<Astra.Api.Validation.PropertyTestValidator>();
// Phase 9.5.a — per-validation-run binary cache (singleton; survives
// across requests so the validator's /validate/falsifying POST and the
// sidecar's /internal/equivalence-callback share the same in-memory map).
builder.Services.AddSingleton<Astra.Api.Validation.PropertyTestRunCache>();

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

// ─── Docs drift detection (Phase 11.0.f) ─────────────────────────────
builder.Services.AddScoped<DriftDetectionService>();

// ─── Docs export (Phase 11.0.g) ───────────────────────────────────────
builder.Services.AddScoped<DocExportService>();

// ─── Project-level artifact bundle export ─────────────────────────────
builder.Services.AddScoped<Astra.Api.Export.ProjectExportService>();

// ─── Phase C — requirements-pack completeness report ──────────────────
builder.Services.AddScoped<Astra.Api.Docs.RequirementsCoverageService>();
builder.Services.AddScoped<Astra.Api.Docs.RequirementsDeliveryService>();

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
    .AllowAnyMethod()
    // Without this the cross-origin frontend can't read download filenames,
    // and file downloads silently fall back to hardcoded names.
    .WithExposedHeaders("Content-Disposition", "X-Astra-Row-Count")));

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
    // CONSUME_ROLL synthetic seed defaults OFF — the Projects dashboard
    // now opens on MINPACK + LAPACK BLAS. Tests that still need
    // CONSUME_ROLL invariants opt in via Database:SeedDemo=true.
    var seedDemo = builder.Configuration.GetValue("Database:SeedDemo", false);
    var seedMinpack = builder.Configuration.GetValue("Database:SeedMinpack", false);
    var seedLapackBlas = builder.Configuration.GetValue("Database:SeedLapackBlas", false);
    var seedIndyDemo = builder.Configuration.GetValue("Database:SeedIndyDemo", false);
    var seedFmtDemo = builder.Configuration.GetValue("Database:SeedFmtDemo", false);
    var seedVb6Demo = builder.Configuration.GetValue("Database:SeedVb6Demo", false);
    var seedMvcMusicStoreDemo = builder.Configuration.GetValue("Database:SeedMvcMusicStoreDemo", false);

    // A brand-new database (no `subroutines` table, so no programme data)
    // gets the schema built from the model the same way RecreateOnStartup
    // does — otherwise the additive ALTER TABLE blocks below crash the
    // first boot of any fresh environment with `relation "subroutines"
    // does not exist`, and the only way in was the wipe-everything flag.
    // The drop is deliberate even here: a half-booted earlier attempt can
    // leave stray tables (platform_configs) that the model script would
    // trip over, and nothing worth keeping can exist without the core.
    var fresh = false;
    if (canConnect && !recreate)
    {
        var probe = await db.Database
            .SqlQueryRaw<bool>("""SELECT to_regclass('public.subroutines') IS NOT NULL AS "Value" """)
            .ToListAsync();
        fresh = probe.Count == 0 || !probe[0];
        if (fresh) Log.Information("Fresh database detected — building the schema from the model");
    }

    if (canConnect && (recreate || fresh))
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
        Log.Information("Schema {Mode} from model ({Bytes} bytes of DDL)", recreate ? "rebuilt" : "created", script.Length);
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

        // Phase 8.0.b — Migration plans + waves. Additive DDL so dev
        // databases pick up without RecreateOnStartup.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS migration_plans (
                id                  uuid        PRIMARY KEY,
                corpus_id           uuid        NOT NULL,
                source_version_id   uuid        NOT NULL,
                status              varchar(16) NOT NULL,
                strategy_name       varchar(64) NOT NULL,
                strategy_options    jsonb       NOT NULL,
                total_routines      integer     NOT NULL,
                total_waves         integer     NOT NULL,
                summary             text        NOT NULL,
                generated_by        varchar(160) NULL,
                approved_by         varchar(160) NULL,
                created_at          timestamptz NOT NULL,
                approved_at         timestamptz NULL,
                archived_at         timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_migration_plans_corpus_status
              ON migration_plans (corpus_id, status);
            CREATE INDEX IF NOT EXISTS ix_migration_plans_version_status
              ON migration_plans (source_version_id, status);

            CREATE TABLE IF NOT EXISTS migration_waves (
                id                  uuid        PRIMARY KEY,
                migration_plan_id   uuid        NOT NULL REFERENCES migration_plans(id) ON DELETE CASCADE,
                wave_number         integer     NOT NULL,
                name                varchar(256) NOT NULL,
                planned_routine_ids jsonb       NOT NULL,
                status              varchar(16) NOT NULL,
                target_start_date   timestamptz NULL,
                target_end_date     timestamptz NULL,
                actual_completed_at timestamptz NULL,
                routine_count       integer     NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_migration_waves_plan_wave
              ON migration_waves (migration_plan_id, wave_number);
            """);

        // Phase 11.0 — Documentation sections + generation runs
        // (per ADR-038). Additive DDL so dev databases pick up without
        // RecreateOnStartup. Column types mirror the EF model in
        // AppDbContext.OnModelCreating; field-level constraints stay
        // owned by the EF side.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS doc_sections (
                id                    uuid        PRIMARY KEY,
                corpus_id             uuid        NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
                source_version_id     uuid        NOT NULL,
                section_kind          varchar(32) NOT NULL,
                scope                 varchar(16) NOT NULL,
                subroutine_id         uuid        NULL REFERENCES subroutines(id) ON DELETE SET NULL,
                module_name           varchar(256) NULL,
                state                 varchar(32) NOT NULL,
                payload_json          jsonb       NOT NULL,
                rendered_markdown     text        NULL,
                llm_call_id           uuid        NULL REFERENCES llm_calls(id) ON DELETE SET NULL,
                generation_run_id     uuid        NULL,
                signature_id          uuid        NULL,
                created_by            uuid        NULL,
                created_at            timestamptz NOT NULL,
                updated_at            timestamptz NOT NULL,
                previous_section_id   uuid        NULL
            );
            CREATE INDEX IF NOT EXISTS ix_doc_sections_corpus_kind
              ON doc_sections (corpus_id, section_kind);
            CREATE INDEX IF NOT EXISTS ix_doc_sections_corpus_state
              ON doc_sections (corpus_id, state);
            CREATE INDEX IF NOT EXISTS ix_doc_sections_subroutine
              ON doc_sections (subroutine_id);
            CREATE INDEX IF NOT EXISTS ix_doc_sections_generation_run
              ON doc_sections (generation_run_id);

            CREATE TABLE IF NOT EXISTS doc_generation_runs (
                id                  uuid        PRIMARY KEY,
                corpus_id           uuid        NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
                source_version_id   uuid        NOT NULL,
                stages_requested    varchar(512) NOT NULL,
                state               varchar(32) NOT NULL,
                metrics_json        jsonb       NULL,
                summary             varchar(1024) NOT NULL,
                error_code          varchar(128) NULL,
                error_summary       varchar(4000) NULL,
                triggered_by        uuid        NULL,
                started_at          timestamptz NOT NULL,
                completed_at        timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_doc_generation_runs_corpus_started
              ON doc_generation_runs (corpus_id, started_at);
            """);

        // WS6 — doc_sections.quality_json (critic score + deterministic checks).
        await Astra.Api.Docs.DocsQualityRegistration.ApplyDocsSchemaAsync(db);

        // Re-sync never stamped source_language (rows sat at the column
        // default 'fortran-f77'); repair from the file extension, the same
        // mapping ingest uses. Idempotent, logs only when it changes rows.
        await Astra.Api.Ingest.SourceLanguageBackfill.ApplyAsync(db);

        // Phase 12.0 — Pattern analysis (bulk extraction + claim-kind
        // clustering). Additive DDL so dev databases pick up without
        // RecreateOnStartup. Column types mirror the EF model in
        // AppDbContext.OnModelCreating.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS pattern_analysis_runs (
                id                  uuid        PRIMARY KEY,
                corpus_id           uuid        NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
                source_version_id   uuid        NOT NULL,
                stages_requested    varchar(128) NOT NULL,
                state               varchar(32) NOT NULL,
                metrics_json        jsonb       NULL,
                summary             varchar(1024) NOT NULL,
                error_summary       varchar(4000) NULL,
                triggered_by        varchar(160) NULL,
                started_at          timestamptz NOT NULL,
                completed_at        timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_pattern_analysis_runs_corpus_started
              ON pattern_analysis_runs (corpus_id, started_at);

            CREATE TABLE IF NOT EXISTS pattern_clusters (
                id                        uuid        PRIMARY KEY,
                pattern_analysis_run_id   uuid        NOT NULL REFERENCES pattern_analysis_runs(id) ON DELETE CASCADE,
                corpus_id                 uuid        NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
                claim_kind_signature      varchar(512) NOT NULL,
                label                     varchar(256) NOT NULL,
                suggested_archetype_name  varchar(128) NOT NULL,
                rationale                 text        NOT NULL,
                member_subroutine_ids     jsonb       NOT NULL,
                member_count              integer     NOT NULL,
                created_at                timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_pattern_clusters_run ON pattern_clusters (pattern_analysis_run_id);
            CREATE INDEX IF NOT EXISTS ix_pattern_clusters_corpus ON pattern_clusters (corpus_id);
            """);

        // Phase 14.0 — Live archetype authoring. Additive DDL so dev
        // databases pick up without RecreateOnStartup. Column types mirror
        // the EF model in AppDbContext.OnModelCreating.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS archetype_proposals (
                id                     uuid        PRIMARY KEY,
                pattern_cluster_id     uuid        NOT NULL REFERENCES pattern_clusters(id) ON DELETE CASCADE,
                corpus_id              uuid        NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
                target_stack           varchar(32) NOT NULL,
                proposed_archetype_id  varchar(128) NOT NULL,
                display_name           varchar(256) NOT NULL,
                description            text        NOT NULL,
                matches_json           jsonb       NOT NULL,
                files_json             jsonb       NOT NULL,
                state                  varchar(32) NOT NULL,
                compile_log            text        NULL,
                compile_error_count    integer     NULL,
                test_count             integer     NULL,
                test_failure_count     integer     NULL,
                llm_call_id            uuid        NULL REFERENCES llm_calls(id) ON DELETE SET NULL,
                generated_by           varchar(160) NULL,
                approved_by            varchar(160) NULL,
                rejected_reason        varchar(2000) NULL,
                created_at             timestamptz NOT NULL,
                verified_at            timestamptz NULL,
                decided_at             timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_archetype_proposals_cluster ON archetype_proposals (pattern_cluster_id);
            CREATE INDEX IF NOT EXISTS ix_archetype_proposals_corpus_state ON archetype_proposals (corpus_id, state);
            CREATE INDEX IF NOT EXISTS ix_archetype_proposals_state ON archetype_proposals (state);
            """);

        // Phase 15.0.a — source_schema was missing entirely: every
        // live-authored archetype was hardcoded to compatibleSchemas =
        // ["unibasic"] regardless of what corpus it was actually proposed
        // from, which would make a java- (or any non-unibasic-) sourced
        // archetype invisible to PickForSubroutine's new schema filter.
        // Backfill existing PRODUCTION rows with "unibasic" — every
        // proposal approved before this column existed really was
        // proposed from a UniBasic corpus.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE archetype_proposals ADD COLUMN IF NOT EXISTS source_schema varchar(32) NOT NULL DEFAULT '';
            UPDATE archetype_proposals SET source_schema = 'unibasic' WHERE source_schema = '';
            """);

        // Phase 15.2 — prompt-cache telemetry on llm_calls. The provider has
        // reported cache_read/cache_creation tokens since Phase 7.0 but the
        // audit row never stored them, so hit-rate and the real (tiered,
        // cache-discounted) cost were invisible.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE llm_calls ADD COLUMN IF NOT EXISTS cache_read_tokens integer NOT NULL DEFAULT 0;
            ALTER TABLE llm_calls ADD COLUMN IF NOT EXISTS cache_creation_tokens integer NOT NULL DEFAULT 0;
            """);

        // Phase 15.2 — per-routine survey digests (the cheap clustering
        // input that replaces full extraction in Pattern Analysis). Column
        // types mirror the EF model in AppDbContext.OnModelCreating.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS routine_digests (
                id                       uuid         PRIMARY KEY,
                subroutine_id            uuid         NOT NULL REFERENCES subroutines(id) ON DELETE CASCADE,
                source_version_id        uuid         NOT NULL,
                source                   varchar(16)  NOT NULL,
                purpose                  text         NOT NULL,
                archetype_hint           varchar(128) NULL,
                claim_kinds_json         jsonb        NOT NULL,
                data_access_json         jsonb        NULL,
                modernization_flags_json jsonb        NULL,
                complexity               varchar(16)  NULL,
                structural_hash          varchar(64)  NULL,
                normalized_token_count   integer      NOT NULL DEFAULT 0,
                exemplar_subroutine_id   uuid         NULL,
                prompt_version           varchar(32)  NULL,
                model                    varchar(128) NULL,
                llm_call_id              uuid         NULL REFERENCES llm_calls(id) ON DELETE SET NULL,
                created_at               timestamptz  NOT NULL,
                updated_at               timestamptz  NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_routine_digests_subroutine ON routine_digests (subroutine_id);
            CREATE INDEX IF NOT EXISTS ix_routine_digests_version ON routine_digests (source_version_id);
            CREATE INDEX IF NOT EXISTS ix_routine_digests_version_hash ON routine_digests (source_version_id, structural_hash);
            """);

        // Phase 15.2 — durable pattern-analysis runs: heartbeat, cancel flag,
        // per-stage checkpoint (see PatternAnalysisOrchestrator).
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE pattern_analysis_runs ADD COLUMN IF NOT EXISTS heartbeat_at timestamptz NULL;
            ALTER TABLE pattern_analysis_runs ADD COLUMN IF NOT EXISTS cancel_requested boolean NOT NULL DEFAULT false;
            ALTER TABLE pattern_analysis_runs ADD COLUMN IF NOT EXISTS checkpoint_json jsonb NULL;
            """);

        // Phase 15.1 — a spec can now be scaffolded onto several target
        // stacks independently instead of the second generation silently
        // overwriting the first. Widen the uniqueness constraint from
        // (spec) to (spec, target_platform): find whatever unique index
        // currently covers spec_id alone (its EF-generated name isn't
        // guaranteed) and replace it, rather than guessing the name.
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE
                old_index_name text;
            BEGIN
                SELECT ix.relname INTO old_index_name
                FROM pg_index i
                JOIN pg_class ix ON ix.oid = i.indexrelid
                JOIN pg_class t ON t.oid = i.indrelid
                WHERE t.relname = 'scaffolds'
                  AND i.indisunique
                  -- indkey is an int2vector; it only compares to int2[] after a cast.
                  AND i.indkey::int2[] = (SELECT array_agg(attnum ORDER BY attnum)
                                           FROM pg_attribute
                                           WHERE attrelid = t.oid AND attname = 'spec_id')::int2[]
                LIMIT 1;

                IF old_index_name IS NOT NULL THEN
                    EXECUTE format('DROP INDEX IF EXISTS %I', old_index_name);
                END IF;
            END $$;

            CREATE UNIQUE INDEX IF NOT EXISTS ix_scaffolds_spec_id_target_platform
              ON scaffolds (spec_id, target_platform);
            """);

        // WS2 — conversation spine (one thread per programme + the global
        // thread; agent/user turns with artifact cards, suggestions, tool
        // calls and pending actions as jsonb). Mirrors AppDbContext.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS conversations (
                id                    uuid         PRIMARY KEY,
                corpus_id             uuid         NULL REFERENCES corpora(id) ON DELETE CASCADE,
                kind                  varchar(24)  NOT NULL,
                title                 varchar(240) NOT NULL,
                created_at            timestamptz  NOT NULL,
                updated_at            timestamptz  NOT NULL,
                last_message_at       timestamptz  NULL,
                last_message_preview  varchar(280) NULL,
                message_count         integer      NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_conversations_corpus_kind ON conversations (corpus_id, kind);
            CREATE TABLE IF NOT EXISTS conversation_messages (
                id                    uuid         PRIMARY KEY,
                conversation_id       uuid         NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role                  varchar(16)  NOT NULL,
                agent                 varchar(32)  NULL,
                persona               varchar(32)  NULL,
                author_display        varchar(160) NULL,
                markdown              text         NOT NULL,
                artifacts_json        jsonb        NOT NULL DEFAULT '[]'::jsonb,
                suggestions_json      jsonb        NOT NULL DEFAULT '[]'::jsonb,
                tool_calls_json       jsonb        NOT NULL DEFAULT '[]'::jsonb,
                pending_action_json   jsonb        NULL,
                llm_turns_json        jsonb        NULL,
                run_id                uuid         NULL,
                created_at            timestamptz  NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversation_messages_conv_created ON conversation_messages (conversation_id, created_at);
            """);

        // WS2 Increment 2 — spec review threads reference their spec.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE conversations ADD COLUMN IF NOT EXISTS ref_id uuid NULL;
            CREATE INDEX IF NOT EXISTS ix_conversations_kind_ref ON conversations (kind, ref_id);
            """);

        // Phase 14.0 — merge any already-approved (PRODUCTION) archetype
        // proposals into the live in-memory registry, so archetypes
        // authored inside the app survive a restart without ever having
        // been written to disk.
        var archetypeRegistry = scope.ServiceProvider.GetRequiredService<Astra.Api.Llm.Archetypes.ArchetypeRegistry>();
        await archetypeRegistry.LoadFromDatabaseAsync(db, CancellationToken.None);
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
    if (canConnect && seedLapackBlas)
    {
        // Same fire-and-forget pattern as MINPACK — LAPACK is a larger
        // clone (~50 MB) so this typically takes 30–90s end-to-end; the
        // API stays responsive throughout and the corpus appears once
        // the background task finishes.
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = scopeFactory.CreateScope();
                var seeder = bgScope.ServiceProvider.GetRequiredService<LapackBlasSeed>();
                await seeder.SeedAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Background LAPACK BLAS seed failed"); }
        });
    }
    if (canConnect && seedIndyDemo)
    {
        // Phase 9.0.h: seed a curated subset of IndySockets/Indy as the
        // headline Delphi corpus. Clone is small (~5MB after whitelist)
        // but parse goes through the delphi-parser-sidecar so end-to-end
        // is similar to MINPACK timing. Background fire-and-forget.
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = scopeFactory.CreateScope();
                var seeder = bgScope.ServiceProvider.GetRequiredService<IndyDemoSeed>();
                await seeder.SeedAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Background Indy demo seed failed"); }
        });
    }
    if (canConnect && seedFmtDemo)
    {
        // Phase 9.1.g: seed a curated subset of fmtlib/fmt as the headline
        // C++ corpus. Background fire-and-forget mirroring Indy.
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = scopeFactory.CreateScope();
                var seeder = bgScope.ServiceProvider.GetRequiredService<FmtDemoSeed>();
                await seeder.SeedAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Background fmt demo seed failed"); }
        });
    }
    if (canConnect && seedVb6Demo)
    {
        // Phase 10.0.h: seed the Nous-authored "VB6 Inventory Sample" as
        // the headline VB6 corpus. Local-only — no network access; the
        // source files ship with the API image under SeedData/.
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = scopeFactory.CreateScope();
                var seeder = bgScope.ServiceProvider.GetRequiredService<Vb6DemoSeed>();
                await seeder.SeedAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Background VB6 demo seed failed"); }
        });
    }
    if (canConnect && seedMvcMusicStoreDemo)
    {
        // Phase 12.0.g: seed Microsoft's MvcMusicStore as the headline C#
        // corpus. Pulls Controllers/, Models/, ViewModels/, App_Start/ from
        // GitHub — ~25 .cs files, ~3k LOC, all targeting .NET Framework 4.6
        // + ASP.NET MVC 5. Ideal demo target for the C# → .NET 10 migration
        // track because it contains every common Framework migration trap.
        var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bgScope = scopeFactory.CreateScope();
                var seeder = bgScope.ServiceProvider.GetRequiredService<MvcMusicStoreSeed>();
                await seeder.SeedAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Background MvcMusicStore demo seed failed"); }
        });
    }
}

// ─── Orphaned-run cleanup ─────────────────────────────────────────────
// Docs, pattern-analysis, and harmonisation runs execute as in-process
// fire-and-forget tasks; a container restart kills them mid-flight and
// leaves QUEUED/RUNNING rows that display as in-progress forever. Mark
// them FAILED at boot. (Single-instance App Service deploy — no risk of
// clobbering another instance's live run.)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        if (await db.Database.CanConnectAsync())
        {
            var bootTime = DateTimeOffset.UtcNow;
            const string orphanNote = "Orphaned: the API restarted while this run was in flight.";
            // Phase 15.2 — pattern-analysis runs are resumable: every digest
            // already written survives, so an interrupted run becomes
            // RESUMABLE (and PatternAnalysisResumeService picks it up) rather
            // than FAILED with hours of work discarded.
            var patternRuns = await db.PatternAnalysisRuns
                .Where(r => r.State == "QUEUED" || r.State == "RUNNING")
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.State, "RESUMABLE")
                    .SetProperty(r => r.Summary, "Interrupted by an API restart — completed digests are kept; resume to continue.")
                    .SetProperty(r => r.CancelRequested, false));
            var docRuns = await db.DocGenerationRuns
                .Where(r => r.State == "QUEUED" || r.State == "RUNNING")
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.State, "FAILED")
                    .SetProperty(r => r.ErrorSummary, orphanNote)
                    .SetProperty(r => r.CompletedAt, bootTime));
            var harmonisationRuns = await db.HarmonisationRuns
                .Where(r => r.Status == "RUNNING")
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, "FAILED")
                    .SetProperty(r => r.ErrorMessage, orphanNote)
                    .SetProperty(r => r.CompletedAt, bootTime));
            if (patternRuns + docRuns + harmonisationRuns > 0)
            {
                Log.Information(
                    "Orphaned-run cleanup: {Pattern} pattern-analysis run(s) → RESUMABLE; marked FAILED — {Docs} docs, {Harm} harmonisation run(s)",
                    patternRuns, docRuns, harmonisationRuns);
            }

            // A routine interrupted mid-extraction stays EXTRACTING, and the
            // pipeline's state guard accepts only PARSED/DRAFT — so without
            // this it can never be extracted again, by any run, forced or
            // not. Restore it to whichever state it came from.
            var revivedDraft = await db.Subroutines
                .Where(s => s.State == "EXTRACTING" && db.Specs.Any(sp => sp.SubroutineId == s.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, "DRAFT"));
            var revivedParsed = await db.Subroutines
                .Where(s => s.State == "EXTRACTING")
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, "PARSED"));
            if (revivedDraft + revivedParsed > 0)
            {
                Log.Information(
                    "Stranded-extraction cleanup: {Draft} routine(s) → DRAFT, {Parsed} → PARSED",
                    revivedDraft, revivedParsed);
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Orphaned-run cleanup failed; stale RUNNING rows may remain");
    }
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseMiddleware<DevPersonaMiddleware>();

app.MapHealthEndpoints();
app.MapWhoamiEndpoint();
app.MapSystemStatsEndpoint();
// Task #178 — apply any UI-stored LLM key before serving, then expose the
// settings surface itself.
await LlmSettingsEndpoints.ApplyStoredLlmKeyAsync(app);
app.MapLlmSettingsEndpoints();
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
app.MapDependencyEndpoints();
app.MapMigrationPlanEndpoints();
app.MapPortfolioEndpoints();
app.MapComplianceEndpoints();
app.MapProviderEndpoints();
app.MapRolesEndpoints();
app.MapValidationPolicyEndpoints();
app.MapSignatureHealthEndpoints();
app.MapCommentEndpoints();
app.MapNotificationEndpoints();
app.MapEvidenceEndpoints();
app.MapDevEndpoints();
app.MapDocsEndpoints();
app.MapProjectExportEndpoints();
app.MapPatternAnalysisEndpoints();
app.MapArchetypeAuthoringEndpoints();
app.MapConversationEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    service = "astra-api",
    version = "0.1.0",
    docs = "/health, /api/v1/whoami"
}));

await app.RunAsync();

// Make Program reachable from tests later
public partial class Program;
