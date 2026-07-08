using Carter;
using Demo.OrderApi;
using Microsoft.EntityFrameworkCore;
using Serilog;

// ── Serilog structured logging ────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

// ── EF Core 10 — DbContext registered as Scoped (safe for request lifetime) ──
// [DI-1] OrderDbContext: Scoped — one context per HTTP request.
builder.Services.AddDbContext<OrderDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default not found in appsettings.json")));

// ── Domain services ───────────────────────────────────────────────────────────
// [DI-2] OrderRepository: Scoped — depends on Scoped DbContext.
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<OrderService>();

// ── HttpClient via factory (replaces WebClient / new HttpClient()) ────────────
builder.Services.AddHttpClient();

// ── Carter for Minimal API feature modules ────────────────────────────────────
builder.Services.AddCarter();

// ── Problem Details (RFC 9457) for all error responses ───────────────────────
builder.Services.AddProblemDetails();

// ── HttpContext accessor (replaces HttpContext.Current [OA-1]) ────────────────
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

// ── Apply pending EF Core migrations on startup (dev only) ───────────────────
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.MigrateAsync();
}

app.MapCarter();

await app.RunAsync();
