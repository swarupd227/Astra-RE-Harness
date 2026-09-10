package com.example.widget;

import java.util.List;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * REST surface the Angular {@code WidgetService}
 * (frontend/src/app/widget.service.ts) calls. Thin by design — every rule
 * lives in {@link WidgetService}; this class only translates HTTP to/from
 * that layer.
 */
@RestController
@RequestMapping("/api/widgets")
@SpecClaim("INV-1")
public class WidgetController {
    private final WidgetService widgetService;

    public WidgetController(WidgetService widgetService) {
        this.widgetService = widgetService;
    }

    @GetMapping
    public List<Widget> list() {
        return widgetService.list();
    }

    @PostMapping
    @SpecClaim("EC-1")
    public ResponseEntity<?> create(@RequestBody NewWidgetRequest request) {
        try {
            var created = widgetService.create(request);
            return ResponseEntity.status(HttpStatus.CREATED).body(created);
        } catch (IllegalArgumentException ex) {
            return ResponseEntity.badRequest().body(ex.getMessage());
        }
    }

    @DeleteMapping("/{id}")
    public ResponseEntity<Void> delete(@PathVariable int id) {
        return widgetService.delete(id)
            ? ResponseEntity.noContent().build()
            : ResponseEntity.notFound().build();
    }
}
