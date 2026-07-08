// SPDX-Spec: php/invoice.php (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// C# projection of PHP invoice.php's lineTotal. Closes two PHP traps:
/// EC-1 money is a <see cref="decimal"/> (never a double) rounded to 2 dp
/// AwayFromZero; LTC-1 the loose <c>$total == "0"</c> becomes an explicit typed
/// comparison, not a string juggle.
/// </summary>
public static class MoneyMath
{
    /// <summary><c>round($price * $qty, 2)</c> as exact decimal money.</summary>
    [SpecClaim("EC-1")]
    [SpecClaim("INV-1")]
    [SpecClaim("LTC-1")]
    [TargetMapping("decimal arithmetic, MidpointRounding.AwayFromZero, 2 dp",
        PhpConstruct = "round($price * $qty, 2) with float money")]
    public static decimal LineTotal(decimal price, int qty)
    {
        if (qty < 0)
            throw new ArgumentException("qty must be non-negative", nameof(qty));

        decimal total = price * qty;
        // LTC-1: the PHP `if ($total == "0")` becomes a typed compare, not a juggle.
        if (total == 0m)
            return 0m;
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }
}
