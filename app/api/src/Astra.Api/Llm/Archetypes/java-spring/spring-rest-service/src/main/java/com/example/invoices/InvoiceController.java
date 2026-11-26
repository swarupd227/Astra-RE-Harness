package com.example.invoices;

import jakarta.validation.Valid;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RestController;

/**
 * The routes the legacy code served, with the same methods, paths, status codes and body shapes: a caller of the
 * old application cannot tell the difference. Business rules live in {@link InvoiceService}; errors are shaped by
 * {@link ApiExceptionHandler}.
 */
@RestController
public class InvoiceController {

    private final InvoiceService service;

    public InvoiceController(InvoiceService service) {
        this.service = service;
    }

    /** 200 with the invoice; 404 {"message"} when there is none. */
    @GetMapping("/invoices/{id}")
    @SpecClaim("HTTP-1")
    public ResponseEntity<Invoice> show(@PathVariable long id) {
        return ResponseEntity.ok(service.find(id));
    }

    /** 201 with {id, amount}; 404 for an unknown customer; 422 {"message", "errors"} for an invalid body. */
    @PostMapping("/customers/{customerId}/invoices")
    @SpecClaim("HTTP-2")
    public ResponseEntity<CreatedInvoice> store(@PathVariable long customerId, @Valid @RequestBody NewInvoiceRequest request) {
        Invoice invoice = service.create(customerId, request);
        return ResponseEntity.status(HttpStatus.CREATED).body(new CreatedInvoice(invoice.id(), invoice.amount()));
    }
}
