// SPDX-Spec: openedge/reserve-stock.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.util.Optional;

/**
 * Port for the ABL record phrases that touch the {@code Item} buffer. The two
 * read methods deliberately preserve the ABL <b>lock mode</b> (RP claims),
 * because the lock mode — not just the WHERE clause — is what the Java target
 * must reproduce:
 *
 * <ul>
 *   <li>{@link #findForUpdate(String)} ⟵ {@code FIND FIRST Item ... EXCLUSIVE-LOCK}
 *       — the row is locked for update inside the transaction.</li>
 *   <li>{@link #findReadOnly(String)} ⟵ {@code FIND FIRST Item ... NO-LOCK}
 *       — a dirty/committed read with no lock.</li>
 * </ul>
 *
 * <p>Both return {@link Optional}: a FIND with no match leaves the ABL buffer
 * {@code NOT AVAILABLE}, which is exactly {@code Optional.empty()} — the
 * not-found guard (RP-3b) becomes {@code isEmpty()}/{@code orElse(...)} instead
 * of the ABL {@code IF AVAILABLE} check.
 *
 * <p>On promotion this port becomes a {@code JpaRepository<Item, String>} and
 * the two methods carry {@code @Lock(PESSIMISTIC_WRITE)} / the default read.
 */
@TargetMapping(value = "JpaRepository<Item,String>",
               ablConstruct = "Item buffer access phrases")
public interface ItemRepositoryPort {

    /** {@code FIND FIRST Item WHERE ItemNum = ? EXCLUSIVE-LOCK} (RP-3). */
    @SpecClaim("RP-3")
    @TargetMapping(value = "@Lock(PESSIMISTIC_WRITE) Optional<Item> findByItemNum(String)",
                   ablConstruct = "FIND FIRST Item ... EXCLUSIVE-LOCK")
    Optional<Item> findForUpdate(String itemNum);

    /** {@code FIND FIRST Item WHERE ItemNum = ? NO-LOCK} (RP read-only). */
    @SpecClaim("RP-3b")
    @TargetMapping(value = "Optional<Item> findByItemNum(String)  // read-only",
                   ablConstruct = "FIND FIRST Item ... NO-LOCK")
    Optional<Item> findReadOnly(String itemNum);

    /** Persist an updated buffer (the ASSIGN under the exclusive lock). */
    @SpecClaim("SE-1")
    Item save(Item item);
}
