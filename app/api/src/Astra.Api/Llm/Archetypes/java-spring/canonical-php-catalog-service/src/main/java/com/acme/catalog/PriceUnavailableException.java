// SPDX-Spec: php/fetch_price.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

/**
 * Typed exception that replaces PHP's silent {@code @}-suppressed failure. Where
 * fetch_price.php swallowed the error and returned 0.0, the migration surfaces
 * it explicitly (EH-1).
 */
public class PriceUnavailableException extends RuntimeException {
    public PriceUnavailableException(String url) {
        super("Remote price unavailable for: " + url);
    }
}
