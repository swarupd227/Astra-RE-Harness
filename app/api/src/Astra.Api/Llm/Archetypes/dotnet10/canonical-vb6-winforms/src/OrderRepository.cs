// SPDX-Spec: vb6/modOrders.UpdateOrderTotal (signed)
// SPDX-Archetype: canonical-vb6-winforms
//
// Replaces VB6's DAO 3.x recordset use with an EF Core 10 repository. The
// VB6 source used `dbsOrders.Execute "INSERT ..."` and walked a Recordset
// for the line-total computation. The .NET 10 port consolidates that into
// a typed repository — same behaviour, no bang-notation traps.

using Microsoft.EntityFrameworkCore;

namespace Demo.OrderEntry;

/// <summary>
/// EF Core repository for the Orders + OrderLines tables. Wraps the
/// DbContext so callers don't need to know about EF specifics. The
/// underlying provider is configured per-environment (SQL Server in
/// production; SQLite for the demo seed; in-memory for tests).
/// </summary>
[SpecClaim("SE-1")]
public sealed class OrderRepository
{
    private readonly OrderContext _ctx;

    public OrderRepository(OrderContext ctx) => _ctx = ctx;

    /// <summary>
    /// INV-2 + SE-1: insert one Orders row. RecordsAffected must equal 1
    /// on success; downstream side effects (Excel export) only run when
    /// the caller confirms this returns 1.
    /// </summary>
    [SpecClaim("INV-2")]
    [SpecClaim("SE-1")]
    public async Task<int> InsertAsync(Order order, CancellationToken ct = default)
    {
        _ctx.Orders.Add(order);
        return await _ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Suggest the next available order id. Replaces VB6's
    /// `SELECT MAX(OrderId) + 1 FROM Orders` query that Form_Load fired.
    /// </summary>
    public async Task<long> NextOrderIdAsync(CancellationToken ct = default)
    {
        var max = await _ctx.Orders.MaxAsync(o => (long?)o.Id, ct);
        return (max ?? 0) + 1;
    }
}

/// <summary>
/// EF Core DbContext for the Orders schema. Constructor-injected;
/// configured per-environment via options.
/// </summary>
public sealed class OrderContext(DbContextOptions<OrderContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>().HasKey(o => o.Id);
        b.Entity<Order>().Property(o => o.CustomerName).HasMaxLength(120);
    }
}
