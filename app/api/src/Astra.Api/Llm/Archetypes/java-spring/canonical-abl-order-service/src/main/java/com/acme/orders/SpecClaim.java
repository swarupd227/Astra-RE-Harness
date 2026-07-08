// SPDX-Spec: openedge/Acme-ERP (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Cites a signed spec/v1 claim id (e.g. "INV-2", "RP-3", "TX-1") on the Java
 * surface that realises it. A reviewer can map every method back to the
 * OpenEdge spec without leaving the IDE, and a future lint step can assert that
 * no signed claim is left unrealised. Framework-free by design so the scaffold
 * compiles offline against the maven-sidecar's baked cache (no Spring needed).
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
