// SPDX-Spec: php/fetch_price.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.math.BigDecimal;
import java.util.Optional;

/**
 * Port for the PHP {@code @file_get_contents($url)} + {@code json_decode} remote
 * price lookup. The fetch/parse is abstracted here; {@link Optional#empty()}
 * models the PHP failure path (file_get_contents returns false → json_decode
 * returns null) so the caller must handle absence EXPLICITLY rather than via the
 * silent {@code @} operator.
 */
@TargetMapping(value = "RestClient / HTTP price gateway bean",
               phpConstruct = "@file_get_contents + json_decode")
public interface RemotePricePort {

    /** The remote price for {@code url}, or empty if unreachable / unparseable. */
    @SpecClaim("SE-1")
    Optional<BigDecimal> fetch(String url);
}
