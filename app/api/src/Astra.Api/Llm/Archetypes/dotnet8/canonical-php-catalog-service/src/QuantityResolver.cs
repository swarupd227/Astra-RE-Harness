// SPDX-Spec: php/qty.php (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// C# projection of PHP qty.php's resolveQty. PHP's <c>??</c> defaults only on
/// absent/null, but the following <c>empty($qty)</c> is true for "0" too, so a
/// real 0 was silently rewritten to 1. NUL-1: the migration maps <c>??</c> to a
/// null/absent default but does NOT reproduce the empty("0") collapse (SME-signed
/// bug fix — a real 0 is preserved). EC-1: the <c>(int)</c> cast becomes a strict
/// parse, not PHP's leading-digit truncation.
/// </summary>
[TargetMapping("service class; ?? → default, (int) → int.Parse", PhpConstruct = "qty.php resolveQty")]
public sealed class QuantityResolver
{
    [SpecClaim("NUL-1")]
    [SpecClaim("EC-1")]
    public int ResolveQty(IReadOnlyDictionary<string, string>? input)
    {
        // ?? : default only when the key is absent/null.
        if (input is null || !input.TryGetValue("qty", out var raw) || raw is null)
            return 1;

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return 1; // an explicitly blank string is "not supplied"

        // EC-1: strict parse (no leading-digit truncation). "0" → 0 (kept).
        if (!int.TryParse(trimmed, out var qty))
            throw new ArgumentException($"qty is not an integer: '{raw}'", nameof(input));
        return qty;
    }
}
