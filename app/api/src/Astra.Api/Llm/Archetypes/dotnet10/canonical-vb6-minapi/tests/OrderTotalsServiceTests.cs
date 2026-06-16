// SPDX-Archetype: canonical-vb6-minapi
//
// xUnit test pack templated against the signed spec's invariants and
// edge cases. The implementer fills arrange/act/assert; the Fact
// attributes + SpecClaim references are scaffolded automatically so
// the spec→test trace stays intact.

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Demo.OrderTotals.Tests;

public sealed class OrderTotalsServiceTests
{
    private static OrderContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<OrderContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrderContext(opts);
    }

    // INV-1: Total = sum of line totals. Currency precision preserved.
    [Fact]
    [SpecClaim("INV-1")]
    [SpecClaim("INV-2")]
    public async Task RecomputeAsync_SumsLines_PreservingCurrencyPrecision()
    {
        await using var ctx = NewCtx();
        var order = new Order { Id = 1001, CustomerName = "ACME" };
        ctx.Orders.Add(order);
        ctx.OrderLines.AddRange(
            new OrderLine { OrderId = 1001, Sku = "A", Quantity = 1, Total = 12.34m },
            new OrderLine { OrderId = 1001, Sku = "B", Quantity = 2, Total = 56.78m });
        await ctx.SaveChangesAsync();

        var svc = new OrderTotalsService(new OrderRepository(ctx), ctx);
        var total = await svc.RecomputeAsync(1001);

        Assert.Equal(69.12m, total);
        Assert.Equal(69.12m, ctx.Orders.Find(1001L)!.Total);
    }

    // EC-1: order has no lines. The routine returns 0 and writes 0 back
    // to Orders.Total without throwing.
    [Fact]
    [SpecClaim("EC-1")]
    public async Task RecomputeAsync_WithNoLines_WritesZero()
    {
        await using var ctx = NewCtx();
        ctx.Orders.Add(new Order { Id = 1002, CustomerName = "EmptyCo" });
        await ctx.SaveChangesAsync();

        var svc = new OrderTotalsService(new OrderRepository(ctx), ctx);
        var total = await svc.RecomputeAsync(1002);

        Assert.Equal(0m, total);
        Assert.Equal(0m, ctx.Orders.Find(1002L)!.Total);
    }

    // Unknown orderId → null result; no exception, no side effect.
    [Fact]
    public async Task RecomputeAsync_WithUnknownOrderId_ReturnsNull()
    {
        await using var ctx = NewCtx();
        var svc = new OrderTotalsService(new OrderRepository(ctx), ctx);

        var total = await svc.RecomputeAsync(9999);

        Assert.Null(total);
    }
}
