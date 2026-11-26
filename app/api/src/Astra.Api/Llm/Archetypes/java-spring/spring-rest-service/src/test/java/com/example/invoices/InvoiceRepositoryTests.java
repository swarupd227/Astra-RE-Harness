package com.example.invoices;

import static org.assertj.core.api.Assertions.assertThat;

import java.math.BigDecimal;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.jdbc.JdbcTest;
import org.springframework.context.annotation.Import;
import org.springframework.jdbc.core.JdbcTemplate;

/**
 * The data access, against a real (embedded) database built from schema.sql: what each statement reads and writes,
 * and that request values travel as bound parameters.
 */
@JdbcTest
@Import(InvoiceRepository.class)
class InvoiceRepositoryTests {

    @Autowired
    private InvoiceRepository invoices;

    @Autowired
    private JdbcTemplate jdbc;

    @BeforeEach
    void aCustomer() {
        jdbc.update("INSERT INTO customers (id, name) VALUES (?, ?)", 3L, "Acme");
    }

    @Test
    @DisplayName("an inserted invoice reads back with its customer, amount and note")
    void insertThenFind() {
        long id = invoices.insert(3L, new BigDecimal("12.50"), "first");
        assertThat(invoices.findById(id)).hasValueSatisfying(inv -> {
            assertThat(inv.customerId()).isEqualTo(3L);
            assertThat(inv.amount()).isEqualByComparingTo("12.50");
            assertThat(inv.note()).isEqualTo("first");
        });
    }

    @Test
    @DisplayName("a missing invoice is empty, not an error")
    void findMissing() {
        assertThat(invoices.findById(424242L)).isEmpty();
    }

    @Test
    @DisplayName("a customer exists only when it is stored")
    void customerExists() {
        assertThat(invoices.customerExists(3L)).isTrue();
        assertThat(invoices.customerExists(4L)).isFalse();
    }

    @Test
    @DisplayName("a value that looks like SQL is stored as text, because it is bound")
    void valuesAreBound() {
        String hostile = "x'); DELETE FROM invoices; --";
        long id = invoices.insert(3L, BigDecimal.ONE, hostile);
        assertThat(invoices.findById(id)).hasValueSatisfying(inv -> assertThat(inv.note()).isEqualTo(hostile));
    }
}
