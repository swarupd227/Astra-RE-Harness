// SPDX-Archetype: canonical-vb6-minapi
//
// Minimal API entry point. Each routine from the signed VB6 spec that the
// archetype matches becomes a typed endpoint registered here. The
// OrderTotalsService holds the business logic; the endpoint is a thin
// translator between HTTP and the typed call.

using Demo.OrderTotals;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrderContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("Orders")
                   ?? "Data Source=orders.db"));
builder.Services.AddScoped<OrderRepository>();
builder.Services.AddScoped<OrderTotalsService>();

var app = builder.Build();

// INV-1: POST /api/orders/{orderId}/recompute-total recomputes the
// Orders.Total field as the sum of OrderLines.Total for that order.
// The endpoint returns the new total in the response body so callers
// can verify without a follow-up GET.
app.MapPost("/api/orders/{orderId:long}/recompute-total",
    async (long orderId, OrderTotalsService svc, CancellationToken ct) =>
    {
        var total = await svc.RecomputeAsync(orderId, ct);
        return total is null
            ? Results.NotFound(new { orderId, error = "order.not_found" })
            : Results.Ok(new { orderId, total });
    })
   .WithName("RecomputeOrderTotal");

app.Run();
