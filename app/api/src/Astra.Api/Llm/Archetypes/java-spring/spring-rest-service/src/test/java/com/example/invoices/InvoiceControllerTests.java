package com.example.invoices;

import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.verifyNoInteractions;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import java.math.BigDecimal;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
import org.springframework.boot.test.mock.mockito.MockBean;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;

/**
 * The HTTP contract, one test per response a caller can get: method, path, status code and body shape, exactly as
 * the signed spec's http_contracts state them.
 */
@WebMvcTest(InvoiceController.class)
class InvoiceControllerTests {

    @Autowired
    private MockMvc mvc;

    @MockBean
    private InvoiceService service;

    @Test
    @DisplayName("GET /invoices/{id} answers 200 with the invoice")
    void showFound() throws Exception {
        when(service.find(7L)).thenReturn(new Invoice(7L, 3L, new BigDecimal("12.50"), "first"));
        mvc.perform(get("/invoices/7"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.id").value(7))
                .andExpect(jsonPath("$.customerId").value(3))
                .andExpect(jsonPath("$.amount").value(12.5))
                .andExpect(jsonPath("$.note").value("first"));
    }

    @Test
    @DisplayName("GET /invoices/{id} answers 404 with a message when there is no such invoice")
    void showMissing() throws Exception {
        when(service.find(99L)).thenThrow(new NotFoundException("No such invoice"));
        mvc.perform(get("/invoices/99"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.message").value("No such invoice"));
    }

    @Test
    @DisplayName("POST /customers/{id}/invoices answers 201 with the new id and amount")
    void storeCreated() throws Exception {
        when(service.create(eq(3L), any(NewInvoiceRequest.class)))
                .thenReturn(new Invoice(11L, 3L, new BigDecimal("5.00"), null));
        mvc.perform(post("/customers/3/invoices").contentType(MediaType.APPLICATION_JSON).content("{\"amount\": 5}"))
                .andExpect(status().isCreated())
                .andExpect(jsonPath("$.id").value(11))
                .andExpect(jsonPath("$.amount").value(5.0));
    }

    @Test
    @DisplayName("POST /customers/{id}/invoices answers 422 with the field errors when the amount is missing")
    void storeInvalid() throws Exception {
        mvc.perform(post("/customers/3/invoices").contentType(MediaType.APPLICATION_JSON).content("{\"note\": \"x\"}"))
                .andExpect(status().isUnprocessableEntity())
                .andExpect(jsonPath("$.message").value("The given data was invalid."))
                .andExpect(jsonPath("$.errors.amount[0]").value("The amount field is required."));
        verifyNoInteractions(service);
    }

    @Test
    @DisplayName("POST /customers/{id}/invoices answers 404 for an unknown customer")
    void storeUnknownCustomer() throws Exception {
        when(service.create(eq(8L), any(NewInvoiceRequest.class))).thenThrow(new NotFoundException("No such customer"));
        mvc.perform(post("/customers/8/invoices").contentType(MediaType.APPLICATION_JSON).content("{\"amount\": 1}"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.message").value("No such customer"));
    }
}
