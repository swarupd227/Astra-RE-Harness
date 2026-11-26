package com.example.invoices;

import jakarta.validation.constraints.DecimalMin;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;
import java.math.BigDecimal;

/**
 * The request body of POST /customers/{customerId}/invoices. Each rule and its message carries over the legacy
 * validation, so a caller that read the old messages still reads the same ones.
 */
@SpecClaim("HTTP-2")
public record NewInvoiceRequest(
        @NotNull(message = "The amount field is required.")
        @DecimalMin(value = "0.01", message = "The amount must be at least 0.01.")
        BigDecimal amount,
        @Size(max = 200, message = "The note may not be greater than 200 characters.")
        String note) {
}
