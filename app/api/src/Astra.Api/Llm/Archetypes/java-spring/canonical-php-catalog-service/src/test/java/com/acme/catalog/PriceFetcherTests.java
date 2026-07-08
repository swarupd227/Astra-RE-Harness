// SPDX-Spec: php/fetch_price.php (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.math.BigDecimal;
import java.util.Optional;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies that the @-suppressed silent failure becomes an EXPLICIT contract (EH-1). */
class PriceFetcherTests {

    /** Fake remote gateway: empty() models file_get_contents==false / json_decode==null. */
    private static RemotePricePort gateway(Optional<BigDecimal> result) {
        return url -> result;
    }

    @Test
    @DisplayName("EH-1: a present price is returned")
    void presentPrice() {
        var f = new PriceFetcher(gateway(Optional.of(new BigDecimal("12.50"))));
        assertThat(f.fetchOrThrow("http://x")).isEqualByComparingTo("12.50");
    }

    @Test
    @DisplayName("EH-1: a failure THROWS rather than silently returning 0.0 (the PHP @ bug)")
    void failureThrows() {
        var f = new PriceFetcher(gateway(Optional.empty()));
        assertThatThrownBy(() -> f.fetchOrThrow("http://down"))
            .isInstanceOf(PriceUnavailableException.class);
    }

    @Test
    @DisplayName("EH-1: fetchOrDefault falls back to an EXPLICIT value, never a silent 0.0")
    void failureFallsBackExplicitly() {
        var f = new PriceFetcher(gateway(Optional.empty()));
        assertThat(f.fetchOrDefault("http://down", new BigDecimal("9.99")))
            .isEqualByComparingTo("9.99");
    }
}
