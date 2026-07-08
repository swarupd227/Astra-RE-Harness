// SPDX-Spec: php/cart.php (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// C# projection of one entry in the PHP cart array (ARR-1). The PHP cart is a
/// single associative array doing duty as both a map (keyed by sku) and a record
/// (the qty/price shape); the record half becomes this immutable type, the map
/// half a Dictionary (see <see cref="ISessionCart"/>). Money is a
/// <see cref="decimal"/>, never a double (EC: PHP money-as-float drifts).
/// </summary>
[SpecClaim("ARR-1")]
[TargetMapping("record (value half of Dictionary<string,CartLine>)",
    PhpConstruct = "$cart[$sku] = ['qty'=>int, 'price'=>float]")]
public sealed record CartLine
{
    public string Sku { get; }
    public int Qty { get; }
    public decimal Price { get; }

    public CartLine(string sku, int qty, decimal price)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new ArgumentException("sku is required", nameof(sku));
        if (qty < 0)
            throw new ArgumentException("qty must be non-negative", nameof(qty));
        Sku = sku;
        Qty = qty;
        Price = price;
    }

    /// <summary>Returns a copy with the quantity increased by <paramref name="delta"/>.</summary>
    public CartLine AddQty(int delta) => new(Sku, Qty + delta, Price);
}
