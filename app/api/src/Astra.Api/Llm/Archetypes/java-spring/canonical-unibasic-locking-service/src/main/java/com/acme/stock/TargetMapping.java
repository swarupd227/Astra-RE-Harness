// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/** Records the Spring construct this element maps to, plus the UniBasic
    construct it replaces (traceability). Documented contract for anything
    that can't compile offline (Spring beans); real code for everything else. */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(TargetMapping.Container.class)
public @interface TargetMapping {
    String value();

    String uniBasicConstruct() default "";

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        TargetMapping[] value();
    }
}
