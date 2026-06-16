// SPDX-Spec: vb6/modOrders.UpdateOrderTotal (signed)
// SPDX-Archetype: canonical-vb6-minapi
//
// .NET 10 / minimal-API projection of VB6 modOrders.UpdateOrderTotal.
// Walks the OrderLines for a given order id, sums the line totals, and
// writes the running total back to the parent Orders row. The VB6
// source used heavy default-property access (rs!Total, rs("Total")) and
// late-bound recordset navigation (MoveNext); both lower to typed
// LINQ + EF Core 10 here.
//
// The implementation BODY is intentionally TODO — the scaffold is a
// contract, not a translation. The signed spec is the source of truth;
// the implementer fills the body and the 4-gate validation pipeline
// (compile / test pack / cross-runtime equivalence / property-based
// search) confirms parity against the original VB6.

using Microsoft.EntityFrameworkCore;

namespace Demo.OrderTotals;

/// <summary>
/// Recomputes Orders.Total as the sum of OrderLines.Total for a given
/// order. Replaces the VB6 modOrders.UpdateOrderTotal routine.
/// </summary>
[SpecClaim("INV-1")]
public sealed class OrderTotalsService(OrderRepository orders, OrderContext ctx)
{
    /// <summary>
    /// INV-1: Total = sum of OrderLines.Total where OrderId matches.
    /// INV-2: Currency precision preserved end-to-end (no Double cast).
    /// EC-1: order has no lines → total is 0; the Orders.Total is still
    /// written so callers see a deterministic value.
    /// SE-1: writes to the Orders.Total field via EF Core SaveChangesAsync.
    /// </summary>
    /// <returns>The recomputed total, or null when the orderId is unknown.</returns>
    [SpecClaim("INV-1")]
    [SpecClaim("INV-2")]
    [SpecClaim("DP-1")]
    [SpecClaim("DP-2")]
    [SpecClaim("LB-1")]
    [SpecClaim("SE-1")]
    [SpecClaim("EC-1")]
    public async Task<decimal?> RecomputeAsync(long orderId, CancellationToken ct = default)
    {
        var order = await ctx.Orders.FindAsync([orderId], ct);
        if (order is null) return null;

        // DP-1 + DP-2: VB6 source used both rsLines!Total (bang) and
        // rsOrder("Total") (parens). Both lower to typed property access
        // on the EF Core entity. The .Sum() form preserves the implicit
        // currency precision the VB6 source relied on.
        // LB-1: rsLines.MoveNext loop becomes a typed LINQ ToListAsync.
        var total = await ctx.OrderLines
            .Where(l => l.OrderId == orderId)
            .Select(l => l.Total)
            .SumAsync(ct);

        order.Total = total;
        await ctx.SaveChangesAsync(ct);
        return total;
    }
}

/// <summary>
/// EF Core entity for an Orders row. Mirrors the VB6 Recordset shape.
/// </summary>
public sealed class Order
{
    public long Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
}

/// <summary>
/// EF Core entity for an OrderLines row. Used by OrderTotalsService and
/// any downstream module (e.g. invoice export, reporting).
/// </summary>
public sealed class OrderLine
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public string Sku { get; set; } = "";
    public long Quantity { get; set; }
    public decimal Total { get; set; }
}
