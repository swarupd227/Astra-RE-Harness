package com.example.widget;

import java.util.List;
import org.springframework.stereotype.Service;

/**
 * Business rules for widgets, independent of both the HTTP layer
 * ({@link WidgetController}) and the storage layer ({@link WidgetRecordPort}).
 */
@Service
@SpecClaim("EC-1")
public class WidgetService {
    private final WidgetRecordPort recordPort;

    public WidgetService(WidgetRecordPort recordPort) {
        this.recordPort = recordPort;
    }

    public List<Widget> list() {
        return recordPort.list();
    }

    /** EC-1: a blank name is rejected rather than silently stored. */
    @SpecClaim("EC-1")
    public Widget create(NewWidgetRequest request) {
        var name = request.name().trim();
        if (name.isEmpty()) {
            throw new IllegalArgumentException("Widget name is required.");
        }
        return recordPort.add(name, request.quantity());
    }

    public boolean delete(int id) {
        return recordPort.remove(id);
    }
}
