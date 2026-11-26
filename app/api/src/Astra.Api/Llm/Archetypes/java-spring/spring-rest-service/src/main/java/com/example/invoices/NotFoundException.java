package com.example.invoices;

/** A record the request names does not exist; answered as 404 with {"message": ...}. */
public class NotFoundException extends RuntimeException {
    public NotFoundException(String message) {
        super(message);
    }
}
