// SPDX-Spec: java/Kiwiplan-MES (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/** Cites a signed spec/v1 modernization claim id (e.g. "MOD-1", "JAK-1", "SB-1"). */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(SpecClaim.Container.class)
public @interface SpecClaim {
    String value();

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        SpecClaim[] value();
    }
}
