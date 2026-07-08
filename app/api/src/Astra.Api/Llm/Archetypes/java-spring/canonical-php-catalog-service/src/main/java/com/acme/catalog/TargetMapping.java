// SPDX-Spec: php/Acme-Storefront (signed)
// SPDX-Archetype: canonical-php-catalog-service
package com.acme.catalog;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Records the Spring Boot 3 construct this element is intended to BECOME once
 * the scaffold is promoted into a live framework project, plus the PHP construct
 * it maps FROM (for traceability). Documented contract, not live wiring — the
 * maven-sidecar compiles this package offline with no Spring on the classpath.
 */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(TargetMapping.Container.class)
public @interface TargetMapping {
    String value();

    String phpConstruct() default "";

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        TargetMapping[] value();
    }
}
