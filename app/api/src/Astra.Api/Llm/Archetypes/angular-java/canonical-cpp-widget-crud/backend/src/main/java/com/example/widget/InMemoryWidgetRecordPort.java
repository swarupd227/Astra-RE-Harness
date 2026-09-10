package com.example.widget;

import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.Collectors;
import org.springframework.stereotype.Component;

/**
 * In-memory reference implementation. Thread-safe via
 * {@link ConcurrentHashMap} so concurrent Angular requests don't corrupt
 * the id sequence (SE-1).
 */
@Component
@SpecClaim("SE-1")
public class InMemoryWidgetRecordPort implements WidgetRecordPort {
    private final Map<Integer, Widget> widgets = new ConcurrentHashMap<>();
    private final AtomicInteger nextId = new AtomicInteger();

    @Override
    public List<Widget> list() {
        return widgets.values().stream()
            .sorted((a, b) -> Integer.compare(a.id(), b.id()))
            .collect(Collectors.toList());
    }

    @Override
    @SpecClaim("INV-1")
    public Widget add(String name, int quantity) {
        var id = nextId.incrementAndGet();
        var widget = new Widget(id, name, quantity);
        widgets.put(id, widget);
        return widget;
    }

    @Override
    public boolean remove(int id) {
        return widgets.remove(id) != null;
    }
}
