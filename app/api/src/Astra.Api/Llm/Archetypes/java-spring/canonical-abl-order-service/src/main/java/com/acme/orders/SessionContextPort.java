// SPDX-Spec: openedge/post-invoice.p, post-batch.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.time.LocalDate;

/**
 * Explicit projection of the ABL {@code DEFINE SHARED VARIABLE gSessionUser} /
 * {@code gPostingDate} session state (SV-1). In ABL these were shared variables
 * read (and mutated) across procedure boundaries with no parameter passing —
 * hidden coupling that is a frequent bug source and has no Java equivalent.
 *
 * <p>The migration makes the dependency EXPLICIT: a constructor-injected port,
 * never a {@code static} mutable field. On promotion this becomes a
 * request-scoped {@code @Component} so each unit of work sees its own user/date.
 */
@SpecClaim("SV-1")
@TargetMapping(value = "@Component @RequestScope SessionContext",
               ablConstruct = "DEFINE SHARED VARIABLE gSessionUser / gPostingDate")
public interface SessionContextPort {

    /** The posting user (⟵ gSessionUser). */
    String user();

    /** The posting date (⟵ gPostingDate). */
    LocalDate postingDate();
}
