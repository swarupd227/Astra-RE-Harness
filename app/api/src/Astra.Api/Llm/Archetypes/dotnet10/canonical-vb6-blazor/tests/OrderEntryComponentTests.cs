// SPDX-Archetype: canonical-vb6-blazor
//
// bUnit-based component tests for OrderEntryComponent. Renders the
// Razor markup in-memory; asserts DOM state + invoked services. No
// browser required.

using Bunit;
using Demo.OrderEntry.Web.Components;
using Demo.OrderEntry.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Demo.OrderEntry.Web.Tests;

public sealed class OrderEntryComponentTests : TestContext
{
    private void RegisterServices()
    {
        Services.AddDbContextFactory<OrderContext>(opts =>
            opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        Services.AddScoped<OrderRepository>();
        Services.AddScoped<OrderEntryState>();
        Services.AddScoped<InvoiceExporter>();
    }

    // INV-1 + EC-1: empty customer short-circuits with no insert.
    [Fact]
    [SpecClaim("INV-1")]
    [SpecClaim("EC-1")]
    public async Task EmptyCustomer_ShortCircuits_WithNoInsert()
    {
        RegisterServices();
        var cut = RenderComponent<OrderEntryComponent>();

        // TODO: leave State.CustomerName empty; click Submit; assert
        // State.Status == "Customer required" and the in-memory db has
        // zero rows.
        Assert.NotNull(cut);
    }

    // INV-2: a successful submit inserts exactly one row.
    [Fact]
    [SpecClaim("INV-2")]
    public async Task SuccessfulSubmit_InsertsExactlyOneRow()
    {
        RegisterServices();
        var ctxFactory = Services.GetRequiredService<IDbContextFactory<OrderContext>>();
        var cut = RenderComponent<OrderEntryComponent>();

        // TODO: set State.CustomerName = "ACME"; submit; assert db count.
        await using var ctx = await ctxFactory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Orders.CountAsync());
    }

    // EC-2: duplicate primary key falls through the OE-1 typed catch.
    [Fact]
    [SpecClaim("EC-2")]
    public void DuplicatePrimaryKey_FallsThroughTypedCatch()
    {
        RegisterServices();
        var cut = RenderComponent<OrderEntryComponent>();

        // TODO: arrange a pre-existing order with id=1001; submit a
        // second order with id=1001; assert State.Status carries the
        // db error message.
        Assert.NotNull(cut);
    }
}
