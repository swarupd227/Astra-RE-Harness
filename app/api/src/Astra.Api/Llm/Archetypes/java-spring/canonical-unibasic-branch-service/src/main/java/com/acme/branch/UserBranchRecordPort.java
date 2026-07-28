// SPDX-Spec: unibasic/add-branch-to-user.pick (signed)
// SPDX-Archetype: canonical-unibasic-branch-service
package com.acme.branch;

import java.util.List;
import java.util.Optional;

/**
 * Explicit projection of the UniBasic hashed-file access in
 * add-branch-to-user.pick (RA-1, FP-1):
 * <pre>
 *   OPEN 'INITIALS' TO USER ELSE STOP
 *   ...
 *   READV BR FROM USER,ID,9 ELSE BR = ''
 *   ...
 *   WRITEV BR ON USER,ID,9
 * </pre>
 * {@code OPEN ... ELSE STOP} is the not-found/unavailable guard — modeled
 * here as this port simply not existing/throwing at the injection boundary,
 * same posture as every other file-open-guard in this platform's archetypes.
 * {@code READV}/{@code WRITEV FROM/ON ...,ID,9} address attribute 9 by BARE
 * NUMBER — field 9 is captured as-is (FP-1); this interface names it
 * {@code branches}, but that name is NOT recoverable from the UniBasic
 * source alone — it requires the client's real DICT (data-dictionary)
 * export to confirm. Flagged here, not invented.
 */
@SpecClaim("RA-1")
public interface UserBranchRecordPort {

    /** The user's branch codes (⟵ attribute 9 of the INITIALS file, unresolved DICT name). */
    @SpecClaim("FP-1")
    Optional<List<String>> findBranches(String userId);

    /** Write the (possibly updated) branch list back (⟵ WRITEV ... ON USER,ID,9). */
    @SpecClaim("FP-1")
    void saveBranches(String userId, List<String> branches);
}
