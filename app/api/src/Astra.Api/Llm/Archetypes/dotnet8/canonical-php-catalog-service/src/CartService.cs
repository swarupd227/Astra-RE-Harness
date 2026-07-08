// SPDX-Spec: php/add_to_cart.php, php/cart.php (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// Explicit projection of the PHP <c>$_SESSION['cart']</c> superglobal (SG-1) —
/// ambient session state lifted to an injected boundary (on promotion an
/// <c>ISession</c>-backed scoped service), never ambient static state.
/// </summary>
[SpecClaim("SG-1")]
[TargetMapping("scoped service over ISession", PhpConstruct = "$_SESSION['cart']")]
public interface ISessionCart
{
    /// <summary>Current cart keyed by sku (⟵ $_SESSION['cart']); never null.</summary>
    IDictionary<string, CartLine> GetCart();

    /// <summary>Upsert a line back into the session cart (a side effect, SE-1).</summary>
    [SpecClaim("SE-1")]
    void PutLine(CartLine line);
}

/// <summary>
/// C# projection of PHP add_to_cart.php + cart.php. Reads/writes the cart via
/// <see cref="ISessionCart"/> instead of touching $_SESSION directly; adding is a
/// side effect (SE-1); the total is exact decimal money (INV-1).
/// </summary>
[TargetMapping("service class", PhpConstruct = "add_to_cart.php / cart.php")]
public sealed class CartService
{
    private readonly ISessionCart _session;

    public CartService(ISessionCart session) => _session = session;

    /// <summary><c>$_SESSION['cart'][$sku] = ($existing ?? 0) + $qty</c>.</summary>
    [SpecClaim("SG-1")]
    [SpecClaim("SE-1")]
    public int AddToCart(string sku, int qty, decimal price)
    {
        if (qty < 0)
            throw new ArgumentException("qty must be non-negative", nameof(qty));

        var cart = _session.GetCart();
        var merged = cart.TryGetValue(sku, out var existing)
            ? existing.AddQty(qty)
            : new CartLine(sku, qty, price);
        _session.PutLine(merged); // SE-1: write-back
        return merged.Qty;
    }

    /// <summary><c>cartTotal($cart)</c> — Σ (qty × price), as exact decimal money.</summary>
    [SpecClaim("ARR-1")]
    [SpecClaim("INV-1")]
    public decimal CartTotal()
    {
        decimal total = 0m;
        foreach (var line in _session.GetCart().Values)
            total += MoneyMath.LineTotal(line.Price, line.Qty);
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }
}
