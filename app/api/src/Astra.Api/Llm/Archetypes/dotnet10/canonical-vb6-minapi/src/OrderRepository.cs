// SPDX-Archetype: canonical-vb6-minapi
//
// Thin wrapper around the OrderContext for the routines that don't need
// the more elaborate OrderTotalsService surface. Same EF Core 10
// repository pattern as the WinForms archetype, scoped to whatever the
// signed VB6 spec needs the minimal-API endpoints to expose.

using Microsoft.EntityFrameworkCore;

namespace Demo.OrderTotals;

[SpecClaim("SE-1")]
public sealed class OrderRepository(OrderContext ctx)
{
    [SpecClaim("INV-2")]
    [SpecClaim("SE-1")]
    public async Task<int> InsertAsync(Order order, CancellationToken ct = default)
    {
        ctx.Orders.Add(order);
        return await ctx.SaveChangesAsync(ct);
    }

    public Task<List<Order>> ListAsync(CancellationToken ct = default) =>
        ctx.Orders.AsNoTracking().ToListAsync(ct);
}

/// <summary>
/// EF Core DbContext for the Orders + OrderLines schema. Configured per-
/// environment via options (SQL Server in prod; SQLite for the demo;
/// in-memory for tests).
/// </summary>
public sealed class OrderContext(DbContextOptions<OrderContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>().HasKey(o => o.Id);
        b.Entity<Order>().Property(o => o.CustomerName).HasMaxLength(120);
        b.Entity<Order>().Property(o => o.Total).HasPrecision(18, 4);

        b.Entity<OrderLine>().HasKey(l => l.Id);
        b.Entity<OrderLine>().Property(l => l.Sku).HasMaxLength(40);
        b.Entity<OrderLine>().Property(l => l.Total).HasPrecision(18, 4);
        b.Entity<OrderLine>().HasIndex(l => l.OrderId);
    }
}
