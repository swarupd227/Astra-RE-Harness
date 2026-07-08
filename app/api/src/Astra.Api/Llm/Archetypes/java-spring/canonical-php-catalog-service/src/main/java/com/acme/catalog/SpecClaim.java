// SPDX-Spec: php/Acme-Storefront (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Cites a signed spec/v1 claim id (e.g. "INV-1", "LTC-1", "SG-1") on the Java
 * surface that realises it. Framework-free so the scaffold compiles offline
 * against the maven-sidecar's baked cache (no Spring needed).
 */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(SpecClaim.Container.class)
public @interface SpecClaim {
    String value();

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        SpecClaim[] value();
    }
}
