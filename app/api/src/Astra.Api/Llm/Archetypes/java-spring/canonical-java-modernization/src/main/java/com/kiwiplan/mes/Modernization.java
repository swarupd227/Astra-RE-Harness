// SPDX-Spec: java/Kiwiplan-MES (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Records the Java 21 / Spring Boot 3 upgrade action this element realises, plus
 * the Java 11 form it replaces (traceability). For framework-level actions that
 * can't be compiled offline (javax→jakarta, Spring Security), this annotation IS
 * the documented contract; for language modernizations, it annotates the real
 * modernized code.
 */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(Modernization.Container.class)
public @interface Modernization {
    /** The Java 21 / SB3 form (e.g. "record", "pattern matching for switch"). */
    String value();

    /** The Java 11 form it replaces (e.g. "verbose POJO", "WebSecurityConfigurerAdapter"). */
    String from() default "";

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        Modernization[] value();
    }
}
