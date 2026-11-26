package com.example.invoices;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.anyLong;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import java.math.BigDecimal;
import java.util.Optional;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

/** The business rules on their own, with the repository stubbed: the order of checks and what gets written. */
class InvoiceServiceTests {

    private final InvoiceRepository repository = mock(InvoiceRepository.class);
    private final InvoiceService service = new InvoiceService(repository);

    @Test
    @DisplayName("an unknown customer is refused before anything is written")
    void unknownCustomer() {
        when(repository.customerExists(8L)).thenReturn(false);
        assertThatThrownBy(() -> service.create(8L, new NewInvoiceRequest(BigDecimal.TEN, null)))
                .isInstanceOf(NotFoundException.class)
                .hasMessage("No such customer");
        verify(repository, never()).insert(anyLong(), any(), any());
    }

    @Test
    @DisplayName("the amount is kept to two places, rounding half up")
    void amountRounding() {
        when(repository.customerExists(3L)).thenReturn(true);
        when(repository.insert(3L, new BigDecimal("10.01"), "n")).thenReturn(5L);
        Invoice created = service.create(3L, new NewInvoiceRequest(new BigDecimal("10.005"), "n"));
        assertThat(created.id()).isEqualTo(5L);
        assertThat(created.amount()).isEqualByComparingTo("10.01");
    }

    @Test
    @DisplayName("a missing invoice is a NotFoundException")
    void missingInvoice() {
        when(repository.findById(9L)).thenReturn(Optional.empty());
        assertThatThrownBy(() -> service.find(9L)).isInstanceOf(NotFoundException.class).hasMessage("No such invoice");
    }
}
