package com.example.invoices;

import java.math.BigDecimal;

/** One stored invoice, as GET /invoices/{id} returns it. Money is BigDecimal, never double. */
@SpecClaim("INV-1")
public record Invoice(long id, long customerId, BigDecimal amount, String note) {
}
