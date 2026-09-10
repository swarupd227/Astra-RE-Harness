// SPDX-Spec: cpp (signed)
// SPDX-Archetype: canonical-cpp-widget-crud
package com.example.widget;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Cites a signed spec/v1 claim id (e.g. "INV-1", "EC-1", "SE-1") on the Java
 * surface that realises it, so a reviewer can map generated code back to the
 * signed C++ spec without leaving the IDE — the Java analog of the C#
 * SpecClaimAttribute used by the dotnet8/angular-dotnet8 archetypes.
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
