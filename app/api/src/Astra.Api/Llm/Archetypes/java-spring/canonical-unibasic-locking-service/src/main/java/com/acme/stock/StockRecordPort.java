// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/**
 * Explicit projection of the UniBasic hashed-file access in
 * reserve-stock.pick (RA-1):
 * <pre>
 *   OPEN 'STOCK' TO STK ELSE STOP
 *   READU QTY.REC FROM STK, ITEM.ID LOCKED ... END ELSE ...
 *   WRITE QTY.REC ON STK, ITEM.ID
 *   RELEASE STK, ITEM.ID
 * </pre>
 * {@code OPEN ... ELSE STOP} is the not-found/unavailable guard for the
 * file itself — modeled here as this port simply not existing/throwing at
 * the injection boundary, same posture as every other file-open-guard in
 * this platform's archetypes. The per-record RELEASE calls in the source
 * are NOT modeled as an explicit port method: they map to normal
 * transaction-boundary semantics on the real target (commit on save,
 * rollback on a thrown domain exception releases the lock either way).
 */
public interface StockRecordPort {

    /** Attempt to acquire an exclusive lock on the record for {@code itemId}
        (⟵ READU ... LOCKED). */
    @SpecClaim("RA-1")
    LockOutcome tryLockForUpdate(String itemId);

    /** Persist the updated record and release the lock (⟵ WRITE ... ON STK,
        ITEM.ID, followed by RELEASE). Must only be called after a
        successful {@link #tryLockForUpdate}. */
    @SpecClaim("RA-1")
    void save(String itemId, StockRecord updated);
}
