// SPDX-Archetype: canonical-vb6-blazor
//
// EF Core repository with the per-request DbContextFactory pattern
// (Blazor Server best-practice — Scoped DbContext leaks across
// re-renders if instantiated naively).

using Microsoft.EntityFrameworkCore;

namespace Demo.OrderEntry.Web.Services;

[SpecClaim("SE-1")]
public sealed class OrderRepository(IDbContextFactory<OrderContext> ctxFactory)
{
    [SpecClaim("INV-2")]
    [SpecClaim("SE-1")]
    public async Task<int> InsertAsync(Order order, CancellationToken ct = default)
    {
        await using var ctx = await ctxFactory.CreateDbContextAsync(ct);
        ctx.Orders.Add(order);
        return await ctx.SaveChangesAsync(ct);
    }

    public async Task<long> NextOrderIdAsync(CancellationToken ct = default)
    {
        await using var ctx = await ctxFactory.CreateDbContextAsync(ct);
        var max = await ctx.Orders.MaxAsync(o => (long?)o.Id, ct);
        return (max ?? 0) + 1;
    }
}

public sealed class Order
{
    public long Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
}

public sealed class OrderContext(DbContextOptions<OrderContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>().HasKey(o => o.Id);
        b.Entity<Order>().Property(o => o.CustomerName).HasMaxLength(120);
        b.Entity<Order>().Property(o => o.Total).HasPrecision(18, 4);
    }
}
