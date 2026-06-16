// SPDX-Archetype: canonical-vb6-blazor
//
// Blazor Server entry point. Registers the EF Core context, the
// scoped form-state service (replaces VB6 form-level fields), and the
// repository. The OrderEntryComponent.razor is rendered at /orders/new.

using Demo.OrderEntry.Web;
using Demo.OrderEntry.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<OrderContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("Orders")
                   ?? "Data Source=orders.db"));
builder.Services.AddScoped<OrderRepository>();

// INV-1 + EH-1: form-level state in VB6 lived on the form class. In
// Blazor Server it lives in a Scoped service so the per-user circuit
// owns the lifetime, NOT the component instance.
builder.Services.AddScoped<OrderEntryState>();

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

// Root App component lives in src/Components/App.razor (not generated
// by the archetype scaffolding — the implementer adds it during code-
// completion).
public sealed partial class App;
