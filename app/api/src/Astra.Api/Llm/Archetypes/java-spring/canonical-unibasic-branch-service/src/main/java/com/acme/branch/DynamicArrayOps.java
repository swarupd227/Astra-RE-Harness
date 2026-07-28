// SPDX-Spec: unibasic/ARRAY.PICK (signed)
// SPDX-Archetype: canonical-unibasic-branch-service
package com.acme.branch;

import java.util.List;

/**
 * Java 21 projection of the ARRAY.PICK / add-branch-to-user.pick LOCATE-then-
 * insert-if-missing idiom (MV-1/MV-2). The UniBasic source does:
 * <pre>
 *   LOCATE NEW IN BR SETTING J THEN
 *      PRINT already-present
 *   ELSE
 *      BR = INSERT(BR,J;NEW)
 *   END
 * </pre>
 * — a multivalue "add if absent" search-and-splice. Once the field lives in
 * a real {@link List}, this collapses to {@link List#contains} +
 * {@link List#add}; there is no angle-bracket position bookkeeping to
 * replicate. The add-branch-to-user.pick source additionally wraps this in a
 * {@code CONVERT VM TO @FM ... CONVERT @FM TO VM} round-trip so the built-in
 * LOCATE/INSERT functions (which only operate on field-mark-delimited data)
 * can act on a value-mark-delimited field — that round-trip is exactly the
 * ceremony a real {@code List<String>} makes unnecessary.
 */
public final class DynamicArrayOps {

    private DynamicArrayOps() {}

    /**
     * @return true if {@code value} was newly added (was absent);
     *         false if it was already present (no-op, matching the source's
     *         "already has it" branch).
     */
    @SpecClaim("MV-1")
    @SpecClaim("MV-2")
    @SpecClaim("INV-1")
    @TargetMapping(value = "List<String>.contains / List<String>.add",
                   uniBasicConstruct = "LOCATE val IN x SETTING j THEN ... ELSE x = INSERT(x,j;val)")
    public static boolean addIfAbsent(List<String> values, String value) {
        if (values.contains(value)) {
            return false;
        }
        values.add(value);
        return true;
    }
}
