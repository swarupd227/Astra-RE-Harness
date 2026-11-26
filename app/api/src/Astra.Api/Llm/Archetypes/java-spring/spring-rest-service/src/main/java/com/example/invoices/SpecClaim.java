package com.example.invoices;

import java.lang.annotation.Repeatable;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/**
 * Cites a signed spec claim id (e.g. "HTTP-1", "DA-1", "INV-1") on the code that realises it, so a reviewer can map
 * the service back to the signed spec of the routine it was migrated from.
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
