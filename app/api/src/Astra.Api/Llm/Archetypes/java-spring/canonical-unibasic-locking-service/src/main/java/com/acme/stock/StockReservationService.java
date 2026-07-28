// SPDX-Spec: unibasic/authored/reserve-stock.pick (signed)
// SPDX-Archetype: canonical-unibasic-locking-service
package com.acme.stock;

/**
 * Java 21 projection of reserve-stock.pick in full (the source is one flat
 * paragraph with no internal GOSUB labels — see unibasic_parser's design
 * note on one-file-one-routine). Reserves {@code requested} units of
 * {@code itemId} under an exclusive record lock, decrementing on-hand
 * quantity, or throws one of three domain exceptions matching the source's
 * three failure branches (RA-1, INV-1).
 */
@TargetMapping(value = "@Service", uniBasicConstruct = "reserve-stock.pick")
public final class StockReservationService {

    private final StockRecordPort port;

    public StockReservationService(StockRecordPort port) {
        this.port = port;
    }

    /**
     * Reserve {@code requested} units of {@code itemId}.
     *
     * @throws ItemLockedException        if another process holds the lock (line 8)
     * @throws ItemNotFoundException      if no record exists for {@code itemId} (line 11)
     * @throws InsufficientStockException if on-hand is strictly less than requested (line 16)
     */
    @SpecClaim("RA-1")
    @SpecClaim("INV-1")
    public void reserve(String itemId, int requested) {
        LockOutcome outcome = port.tryLockForUpdate(itemId);

        if (outcome instanceof LockOutcome.LockedByAnotherUser) {
            throw new ItemLockedException(itemId);
        }
        if (outcome instanceof LockOutcome.NotFound) {
            throw new ItemNotFoundException(itemId);
        }

        StockRecord record = ((LockOutcome.Found) outcome).record();
        int onHand = record.onHand();

        // Strictly less-than (INV-1): a requested quantity exactly equal to
        // on-hand is allowed and reduces on-hand to zero.
        if (onHand < requested) {
            throw new InsufficientStockException(itemId, onHand, requested);
        }

        port.save(itemId, new StockRecord(onHand - requested));
    }
}
