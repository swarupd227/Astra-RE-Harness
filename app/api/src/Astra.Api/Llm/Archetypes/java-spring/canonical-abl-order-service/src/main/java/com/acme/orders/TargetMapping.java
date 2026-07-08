// SPDX-Spec: openedge/Acme-ERP (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Records the Spring Boot 3 / Spring Data JPA construct this element is intended
 * to BECOME once the scaffold is promoted into a live framework project. It is a
 * documented contract, not a live wiring — the maven-sidecar compiles this
 * package OFFLINE with no Spring/JPA on the classpath, so the mapping lives here
 * as metadata instead of as an unresolvable {@code @Repository}/{@code @Service}.
 *
 * <p>Example: an ABL {@code FIND FIRST Item ... EXCLUSIVE-LOCK} becomes
 * {@code @TargetMapping(value = "@Lock(PESSIMISTIC_WRITE) JpaRepository method",
 *   ablConstruct = "FIND FIRST ... EXCLUSIVE-LOCK")}.
 */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(TargetMapping.Container.class)
public @interface TargetMapping {
    /** The Spring/JPA construct to generate on promotion. */
    String value();

    /** The ABL construct this maps FROM (for traceability). */
    String ablConstruct() default "";

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        TargetMapping[] value();
    }
}
