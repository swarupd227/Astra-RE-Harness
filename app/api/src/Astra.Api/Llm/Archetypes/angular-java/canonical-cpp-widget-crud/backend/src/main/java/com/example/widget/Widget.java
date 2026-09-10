package com.example.widget;

/** A stored widget, as returned to the Angular client. */
@SpecClaim("INV-1")
public record Widget(int id, String name, int quantity) {
}
