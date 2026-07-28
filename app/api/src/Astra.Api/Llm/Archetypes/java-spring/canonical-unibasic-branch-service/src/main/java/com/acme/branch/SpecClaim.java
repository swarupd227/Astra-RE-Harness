// SPDX-Spec: unibasic/add-branch-to-user.pick, unibasic/ARRAY.PICK (signed)
// SPDX-Archetype: canonical-unibasic-branch-service
package com.acme.branch;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/** Cites a signed spec/v1 claim id (e.g. "MV-1", "FP-1", "RA-1"). */
@Retention(RetentionPolicy.RUNTIME)
@Repeatable(SpecClaim.Container.class)
public @interface SpecClaim {
    String value();

    @Retention(RetentionPolicy.RUNTIME)
    @interface Container {
        SpecClaim[] value();
    }
}
