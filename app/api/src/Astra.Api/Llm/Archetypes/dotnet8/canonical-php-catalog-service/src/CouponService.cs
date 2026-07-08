// SPDX-Spec: php/discount.php (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// C# projection of PHP discount.php's applyCoupon. The PHP source used loose
/// <c>==</c> that mis-handles coupon codes ("0" == false is true; pre-PHP-8
/// 0 == "SAVE10" is true). LTC-1: the migration uses an EXPLICIT typed check — a
/// coupon is absent only when null/whitespace, and codes match by value; the
/// string "0" is a real, non-empty code and is NOT treated as false.
/// </summary>
[TargetMapping("service class (DI-registered)", PhpConstruct = "discount.php applyCoupon")]
public sealed class CouponService
{
    private const decimal Save10Rate = 0.10m;

    [SpecClaim("LTC-1")]
    [SpecClaim("INV-1")]
    public decimal ApplyCoupon(decimal subtotal, string? couponCode)
    {
        // LTC-1: "absent" is null-or-whitespace ONLY — "0" is a real code.
        if (string.IsNullOrWhiteSpace(couponCode))
            return subtotal;

        decimal rate = couponCode == "SAVE10" ? Save10Rate : 0m; // === semantics
        decimal discounted = subtotal - (subtotal * rate);
        return Math.Round(discounted, 2, MidpointRounding.AwayFromZero);
    }
}
