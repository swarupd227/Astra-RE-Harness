package com.example.invoices;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

/** The service's entry point. The data source comes from spring.datasource.* (an embedded H2 database when unset). */
@SpringBootApplication
public class InvoiceApiApplication {
    public static void main(String[] args) {
        SpringApplication.run(InvoiceApiApplication.class, args);
    }
}
