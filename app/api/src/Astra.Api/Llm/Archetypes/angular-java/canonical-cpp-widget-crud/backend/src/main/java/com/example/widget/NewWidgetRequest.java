package com.example.widget;

/** The create-request payload — no id; the record port assigns one. */
public record NewWidgetRequest(String name, int quantity) {
}
