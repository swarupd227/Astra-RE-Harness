using Asp.Versioning;
using Demo.CustomerApi;
using Demo.CustomerApi.Services;
using Demo.CustomerApi.Repositories;
using Demo.CustomerApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

// ── EF Core 10 — Scoped DbContext (one per request) ──────────────────────────
// [DI-2] AppDbContext: Scoped — replaces DependencyResolver.GetService<DbContext>
//        and new DbContext() instantiation patterns from Web API 2.
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default not found")));

// ── Domain services via constructor injection ─────────────────────────────────
// [DI-1] Replaces DependencyResolver, GlobalConfiguration.Configuration.DependencyResolver,
//        and static ServiceLocator calls from the Framework app.
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<CustomerService>();

// ── HttpClient factory (replaces new HttpClient() and WebClient) ─────────────
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

// ── [ApiController] + versioning + OpenAPI ────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddApiVersioning(opt =>
{
    opt.DefaultApiVersion = new ApiVersion(1, 0);
    opt.AssumeDefaultVersionWhenUnspecified = true;
    opt.ReportApiVersions = true;
}).AddApiExplorer(opt =>
{
    opt.GroupNameFormat = "'v'VVV";
    opt.SubstituteApiVersionInUrl = true;
});

// ── Problem Details (RFC 9457) for all error responses ───────────────────────
// [EH-1] Replaces HttpResponseException and HttpError from Web API 2.
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

await app.RunAsync();
