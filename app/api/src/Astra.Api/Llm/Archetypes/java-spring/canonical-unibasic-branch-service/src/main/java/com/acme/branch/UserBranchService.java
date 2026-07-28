// SPDX-Spec: unibasic/add-branch-to-user.pick (signed)
// SPDX-Archetype: canonical-unibasic-branch-service
package com.acme.branch;

import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/**
 * Java 21 projection of add-branch-to-user.pick's {@code DOIT:} paragraph
 * (an internal GOSUB paragraph in the source, not a separate routine — see
 * unibasic_parser's design note). The source runs a SELECT/READNEXT/REPEAT
 * cursor over every user whose HOME_BR isn't "PT" and NO.LOGON isn't "1"
 * (RA-1), then for each matching user: converts field 9's value-mark
 * delimited branch list to field-mark delimiting (MV-1), LOCATEs the
 * requested branch, and INSERTs it if absent, converting back before the
 * WRITEV. That whole conversion dance becomes a plain
 * {@link DynamicArrayOps#addIfAbsent} call once the field lives in a real
 * {@code List<String>} — see {@link DynamicArrayOps} for why.
 *
 * <p>This class covers the per-user branch-add operation; the SELECT/
 * READNEXT cursor over "every eligible user" (RA-1) is a Spring Data query
 * on the real target and is out of scope for this feasibility slice.
 */
@TargetMapping(value = "@Service", uniBasicConstruct = "add-branch-to-user.pick DOIT:")
public final class UserBranchService {

    private final UserBranchRecordPort port;

    public UserBranchService(UserBranchRecordPort port) {
        this.port = port;
    }

    /**
     * Add {@code branch} to the user's branch list if not already present.
     *
     * @return true if the branch was newly added; false if the user already
     *         had it (matches the source's "already has it" print-and-skip
     *         branch — no write occurs in that case).
     */
    @SpecClaim("MV-1")
    @SpecClaim("RA-1")
    @SpecClaim("INV-1")
    public boolean addBranchIfAbsent(String userId, String branch) {
        Optional<List<String>> existing = port.findBranches(userId);
        List<String> branches = existing.map(ArrayList::new).orElseGet(ArrayList::new);

        boolean added = DynamicArrayOps.addIfAbsent(branches, branch);
        if (added) {
            port.saveBranches(userId, branches);
        }
        return added;
    }
}
