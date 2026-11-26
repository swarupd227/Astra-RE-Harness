package com.example.invoices;

import java.math.BigDecimal;

/** The body of a 201 from POST /customers/{customerId}/invoices: the same fields the legacy handler returned. */
@SpecClaim("HTTP-2")
public record CreatedInvoice(long id, BigDecimal amount) {
}
