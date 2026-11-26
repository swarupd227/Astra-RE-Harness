package com.example.invoices;

import java.math.BigDecimal;
import java.math.RoundingMode;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

/** The routine's business rules, apart from HTTP: what is checked, in what order, and what is written. */
@Service
public class InvoiceService {

    private final InvoiceRepository invoices;

    public InvoiceService(InvoiceRepository invoices) {
        this.invoices = invoices;
    }

    @SpecClaim("HTTP-1")
    public Invoice find(long id) {
        return invoices.findById(id).orElseThrow(() -> new NotFoundException("No such invoice"));
    }

    /** The customer must exist before anything is written; the amount is kept to two places, rounding half up. */
    @Transactional
    @SpecClaim("INV-1")
    @SpecClaim("EC-1")
    public Invoice create(long customerId, NewInvoiceRequest request) {
        if (!invoices.customerExists(customerId)) {
            throw new NotFoundException("No such customer");
        }
        BigDecimal amount = request.amount().setScale(2, RoundingMode.HALF_UP);
        long id = invoices.insert(customerId, amount, request.note());
        return new Invoice(id, customerId, amount, request.note());
    }
}
