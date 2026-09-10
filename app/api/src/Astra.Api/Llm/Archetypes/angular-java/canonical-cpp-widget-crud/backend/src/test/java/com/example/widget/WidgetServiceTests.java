package com.example.widget;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

class WidgetServiceTests {

    private static WidgetService newService() {
        return new WidgetService(new InMemoryWidgetRecordPort());
    }

    @Test
    void create_AssignsIdAndStoresWidget() {
        var service = newService();

        var created = service.create(new NewWidgetRequest("Bolt", 10));

        assertTrue(created.id() > 0);
        assertEquals("Bolt", created.name());
        assertEquals(10, created.quantity());
        assertTrue(service.list().stream().anyMatch(w -> w.id() == created.id()));
    }

    @ParameterizedTest
    @ValueSource(strings = {"", "   "})
    void create_RejectsBlankName(String blankName) {
        var service = newService();

        assertThrows(IllegalArgumentException.class,
            () -> service.create(new NewWidgetRequest(blankName, 1)));
    }

    @Test
    void delete_RemovesExistingWidget() {
        var service = newService();
        var created = service.create(new NewWidgetRequest("Nut", 5));

        var removed = service.delete(created.id());

        assertTrue(removed);
        assertFalse(service.list().stream().anyMatch(w -> w.id() == created.id()));
    }

    @Test
    void delete_ReturnsFalseForMissingWidget() {
        var service = newService();

        assertFalse(service.delete(999));
    }
}
