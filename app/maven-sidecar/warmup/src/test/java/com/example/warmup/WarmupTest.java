package com.example.warmup;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.api.Test;

/**
 * A trivial JUnit 5 test that exists ONLY so the image-build-time `mvn test`
 * forces Surefire to resolve and cache its JUnit Platform provider
 * (org.apache.maven.surefire:surefire-junit-platform) plus the junit-platform
 * launcher/engine jars. Without a real test run at build time, those provider
 * artifacts never land in the baked ~/.m2, and every generated scaffold's
 * `mvn -o test` fails offline with "surefire-junit-platform ... (absent)".
 * assertj is exercised too so its transitive chain is cached.
 */
class WarmupTest {

    @Test
    void junitPlatformProviderIsCached() {
        assertThat(1 + 1).isEqualTo(2);
    }
}
