// SPDX-Spec: openedge/apply-discount.p (signed)
// SPDX-Archetype: canonical-abl-order-service
package com.acme.orders;

import java.math.BigDecimal;

/**
 * Java 21 projection of ABL {@code apply-discount.p}. This is the Unknown-value
 * ({@code ?}) trap made concrete.
 *
 * <p><b>Q-1 (open question) resolved:</b> the ABL Unknown value is represented
 * in Java as a {@code null BigDecimal}. That choice is load-bearing and was
 * confirmed on sign-off, because the Unknown value is DISTINCT from both null-
 * as-absence and from zero, and it <i>propagates</i>: any arithmetic involving
 * {@code ?} yields {@code ?} (EC-3). We reproduce that propagation explicitly
 * rather than letting {@code base.subtract(null)} throw — a {@code ?} discount
 * must make the net {@code ?}, NOT the base price, until the guard fires.
 *
 * <p>ABL source:
 * <pre>
 *   pdNet = pdBase - pdDiscount.        /* ? propagates: ?-anything = ? *&#47;
 *   IF pdNet = ? THEN pdNet = pdBase.   /* explicit Unknown guard *&#47;
 * </pre>
 */
@TargetMapping(value = "@Service; Unknown value ? ⟶ null BigDecimal",
               ablConstruct = "apply-discount.p")
public final class DiscountCalculator {

    /**
     * @param base     the base price (required; a {@code ?} base is out of scope
     *                 for this procedure and treated as a caller error)
     * @param discount the discount, or {@code null} to model the ABL Unknown
     *                 value ({@code ?})
     * @return {@code base - discount}, except that a {@code ?} (null) discount
     *         propagates to a {@code ?} net which the guard then restores to
     *         {@code base} — never silently zero.
     */
    @SpecClaim("EC-3")
    @SpecClaim("INV-3")
    @SpecClaim("Q-1")
    public BigDecimal applyDiscount(BigDecimal base, BigDecimal discount) {
        if (base == null) {
            throw new IllegalArgumentException("base price is required (a ? base is unsupported here)");
        }

        // pdNet = pdBase - pdDiscount  — reproduce ? propagation: if the
        // discount is Unknown (null), the net is Unknown (null), NOT base-0.
        BigDecimal net = (discount == null) ? null : base.subtract(discount);

        // IF pdNet = ? THEN pdNet = pdBase  — the explicit Unknown guard.
        if (net == null) {
            net = base;
        }
        return net;
    }
}
