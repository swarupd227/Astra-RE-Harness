// SPDX-Spec: cpp (signed)
// SPDX-Archetype: canonical-cpp-widget-crud
//
// Spring Boot 3 Web API host for the Angular frontend in ../frontend.
// Component scanning under com.example.widget wires the widget vertical
// (record port + service + controller) automatically.
package com.example.widget;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

@SpringBootApplication
public class WidgetApiApplication {
    public static void main(String[] args) {
        SpringApplication.run(WidgetApiApplication.class, args);
    }
}
