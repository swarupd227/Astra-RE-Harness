// SPDX-Archetype: canonical-vb6-blazor
//
// Phase 10.3 — moved out of OrderEntryComponent.razor.cs so the
// Razor compiler can resolve the @inject reference. Lived in the
// component file originally but Razor's namespace inference doesn't
// reach back through nested partial-class siblings.
//
// Placeholder for the invoice exporter — implementer wires this up at
// scaffold time. For Blazor Server, ClosedXML output is streamed to the
// browser via a download endpoint or saved to the customer-configured
// shared storage; the choice is an Open Question on the spec.

namespace Demo.OrderEntry.Web.Services;

public sealed class InvoiceExporter
{
    public Task ExportInvoiceAsync(long orderId)
    {
        // TODO: stream the .xlsx to the browser or write to shared storage.
        return Task.CompletedTask;
    }
}
