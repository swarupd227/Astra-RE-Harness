// SPDX-Spec: java/BatchPlanner.java (signed)
// SPDX-Archetype: canonical-java-modernization
package com.kiwiplan.mes;

import java.util.ArrayList;
import java.util.List;

/**
 * EC-1: the unmodifiable-{@code toList()} edge case. The Java-11 source did
 * {@code collect(Collectors.toList())} (a mutable ArrayList) and then MUTATED the
 * result with {@code .add("EXPEDITE")}. A naive modernization to
 * {@code Stream.toList()} would break it — {@code Stream.toList()} returns an
 * UNMODIFIABLE list, so the {@code .add} throws {@link UnsupportedOperationException}.
 *
 * <p>The correct modernization keeps a MUTABLE list: wrap {@code Stream.toList()}
 * in a {@code new ArrayList<>(...)} (or keep {@code collect(toList())}). MOD-1 is
 * only safe where the result is NOT mutated. INV-1: SKUs with qty &gt; 10, plus
 * the appended EXPEDITE marker.
 */
@Modernization(value = "Stream.toList() wrapped in a mutable ArrayList (it is mutated)",
               from = "collect(Collectors.toList())")
public final class BatchPlanner {

    @SpecClaim("EC-1")
    @SpecClaim("INV-1")
    public List<String> highPriority(List<WorkItem> items) {
        // A MUTABLE copy — Stream.toList() alone is unmodifiable and the add below
        // would throw. This is exactly the edge case a mechanical upgrade misses.
        List<String> result = new ArrayList<>(
            items.stream()
                .filter(i -> i.qty() > 10)
                .map(WorkItem::sku)
                .toList());
        result.add("EXPEDITE");
        return result;
    }
}
