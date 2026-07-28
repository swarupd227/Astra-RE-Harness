// SPDX-Spec: unibasic/add-branch-to-user.pick (signed)
// SPDX-Archetype: canonical-unibasic-branch-service
package com.acme.branch;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** Verifies the injected-port replacement for OPEN...READV/WRITEV field 9 (RA-1/FP-1). */
class UserBranchServiceTests {

    /** In-memory fake of the INITIALS file's field-9 (branch list) attribute. */
    private static final class FakePort implements UserBranchRecordPort {
        final Map<String, List<String>> records = new LinkedHashMap<>();

        @Override
        public Optional<List<String>> findBranches(String userId) {
            return Optional.ofNullable(records.get(userId)).map(List::copyOf);
        }

        @Override
        public void saveBranches(String userId, List<String> branches) {
            records.put(userId, List.copyOf(branches));
        }
    }

    @Test
    @DisplayName("RA-1/MV-1: adds a new branch and writes it back through the port")
    void addsAndPersists() {
        var port = new FakePort();
        port.records.put("JSMITH", List.of("1", "2"));
        var svc = new UserBranchService(port);

        boolean added = svc.addBranchIfAbsent("JSMITH", "3");

        assertThat(added).isTrue();
        assertThat(port.records.get("JSMITH")).containsExactly("1", "2", "3");
    }

    @Test
    @DisplayName("Already-present branch is a no-op — no write occurs (matches the source's print-and-skip branch)")
    void noWriteWhenAlreadyPresent() {
        var port = new FakePort();
        port.records.put("JSMITH", List.of("1", "2"));
        var svc = new UserBranchService(port);

        boolean added = svc.addBranchIfAbsent("JSMITH", "2");

        assertThat(added).isFalse();
        assertThat(port.records.get("JSMITH")).containsExactly("1", "2");
    }

    @Test
    @DisplayName("FP-1: a user with no existing branch-9 record starts from an empty list, not null")
    void missingRecordStartsEmpty() {
        var port = new FakePort();
        var svc = new UserBranchService(port);

        boolean added = svc.addBranchIfAbsent("NEWUSER", "PT");

        assertThat(added).isTrue();
        assertThat(port.records.get("NEWUSER")).containsExactly("PT");
    }
}
