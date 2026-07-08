// SPDX-Spec: php/qty.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.util.Map;

/**
 * Java 21 projection of PHP {@code qty.php}'s {@code resolveQty}. The PHP source
 * had two absence checks with DIFFERENT semantics, and their interaction is a
 * latent bug:
 * <pre>
 *   $qty = $input['qty'] ?? 1;   // ?? defaults only on absent/null ("0" survives)
 *   if (empty($qty)) $qty = 1;   // but empty("0") is TRUE, so a real 0 → 1
 * </pre>
 *
 * <p>NUL-1: the migration maps {@code ??} to a null/absent default but does NOT
 * reproduce the {@code empty("0")} collapse — a legitimate quantity of 0 is
 * preserved. The SME confirmed on sign-off that the empty("0")→1 behaviour was
 * an unintended bug, so the target fixes it (documented, not silently copied).
 * EC-1: the {@code (int)} cast maps to a strict parse that rejects garbage
 * rather than PHP's leading-digit truncation.
 */
@TargetMapping(value = "@Service; ?? → getOrDefault, (int) → strict parse",
               phpConstruct = "qty.php resolveQty")
public final class QuantityResolver {

    /**
     * @param input the request parameters (⟵ $_POST/$_GET, lifted to an explicit map)
     * @return the resolved quantity: the parsed value when present (0 preserved),
     *         else the default of 1
     */
    @SpecClaim("NUL-1")
    @SpecClaim("EC-1")
    public int resolveQty(Map<String, String> input) {
        // ?? : default only when the key is absent or null.
        String raw = (input == null) ? null : input.get("qty");
        if (raw == null) {
            return 1;
        }
        String trimmed = raw.trim();
        if (trimmed.isEmpty()) {
            // An explicitly blank string is "not supplied" → default. This is the
            // ONLY empty-ish case we default on; a real "0" is NOT defaulted.
            return 1;
        }
        try {
            // EC-1: strict parse (no PHP leading-digit truncation). "0" → 0 (kept).
            return Integer.parseInt(trimmed);
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException("qty is not an integer: '" + raw + "'", e);
        }
    }
}
