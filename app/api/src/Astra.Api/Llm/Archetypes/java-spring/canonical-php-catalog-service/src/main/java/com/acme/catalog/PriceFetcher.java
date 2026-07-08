// SPDX-Spec: php/fetch_price.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.math.BigDecimal;
import java.util.Optional;

/**
 * Java 21 projection of PHP {@code fetch_price.php}. The PHP source used
 * {@code @file_get_contents} (suppresses the warning) and {@code json_decode}
 * (returns null on failure), so a network/parse failure was silently swallowed
 * and the caller received {@code 0.0} — indistinguishable from a real zero price.
 *
 * <p>EH-1: the migration makes the failure EXPLICIT. Two flavours are provided so
 * the SME can choose the contract on sign-off:
 * <ul>
 *   <li>{@link #fetchOrThrow(String)} — throw {@link PriceUnavailableException}
 *       (recommended when a missing price must halt checkout).</li>
 *   <li>{@link #fetchOrDefault(String, BigDecimal)} — fall back to an EXPLICIT
 *       caller-supplied default (never a silent 0.0).</li>
 * </ul>
 */
@TargetMapping(value = "@Service", phpConstruct = "fetch_price.php")
public final class PriceFetcher {

    private final RemotePricePort port;

    public PriceFetcher(RemotePricePort port) {
        this.port = port;
    }

    /** Fetch the price or throw — the failure is never swallowed. */
    @SpecClaim("EH-1")
    public BigDecimal fetchOrThrow(String url) {
        return port.fetch(url).orElseThrow(() -> new PriceUnavailableException(url));
    }

    /** Fetch the price or fall back to an EXPLICIT default (not a silent 0.0). */
    @SpecClaim("EH-1")
    public BigDecimal fetchOrDefault(String url, BigDecimal fallback) {
        if (fallback == null) {
            throw new IllegalArgumentException("fallback is required (no silent 0.0)");
        }
        Optional<BigDecimal> price = port.fetch(url);
        return price.orElse(fallback);
    }
}
