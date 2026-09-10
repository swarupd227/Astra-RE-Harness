package com.example.widget;

import java.util.List;

/**
 * Data-access seam for widgets — the Java projection of whatever the
 * legacy C++ routine actually persisted to (a file, a struct array, an
 * external service). Swap the implementation for a real store; the
 * service and controller layers above it don't change.
 */
public interface WidgetRecordPort {
    List<Widget> list();

    Widget add(String name, int quantity);

    boolean remove(int id);
}
