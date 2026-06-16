// SPDX-Spec: vb6/frmOrderEntry (signed)
// SPDX-Archetype: canonical-vb6-blazor
//
// Code-behind for OrderEntryComponent.razor. The load-bearing
// translation:
//   * EH-1: btnSubmit_Click → OnSubmitAsync (async Task)
//   * EH-1: Form_Load       → OnInitializedAsync (DOM NOT ready)
//   * OE-1: On Error Goto   → typed try/catch on DbException with the
//                              same Err.Number filter as the WinForms
//                              archetype
//   * INV-1 + EC-1: empty customer short-circuits with no DB write
//   * INV-2 + SE-1: exactly-once insert; invoice export only on success

using System.Data.Common;
using Demo.OrderEntry.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Demo.OrderEntry.Web.Components;

[SpecClaim("EH-1")]
public sealed partial class OrderEntryComponent : ComponentBase
{
    [Inject] private OrderEntryState State { get; set; } = null!;
    [Inject] private OrderRepository Orders { get; set; } = null!;
    [Inject] private InvoiceExporter Exporter { get; set; } = null!;

    /// <summary>
    /// EH-1: Form_Load equivalent. Runs once per circuit before the
    /// component renders. The DOM does NOT exist yet — reads that
    /// depend on rendered controls must move to OnAfterRenderAsync.
    /// </summary>
    /// <remarks>
    /// VB6 source used cmbProducts.AddItem and read the next OrderId
    /// during Form_Load. The Blazor equivalent pre-populates the
    /// scoped state so the first render carries the right defaults.
    /// </remarks>
    [SpecClaim("EH-1")]
    [SpecClaim("INV-1")]
    protected override async Task OnInitializedAsync()
    {
        State.OrderId = await Orders.NextOrderIdAsync();
        // TODO: populate State.Products from the repository.
    }

    /// <summary>
    /// EH-1 + INV-1: btnSubmit_Click → OnSubmitAsync. Same business
    /// invariants as the WinForms archetype; the paradigm-specific
    /// differences are: (a) async/await throughout, (b) no MessageBox
    /// — failures surface via State.Status which the markup re-renders,
    /// (c) the form re-renders automatically after the await.
    /// </summary>
    [SpecClaim("EH-1")]
    [SpecClaim("INV-1")]
    [SpecClaim("INV-2")]
    [SpecClaim("OE-1")]
    [SpecClaim("DP-1")]
    [SpecClaim("SE-1")]
    [SpecClaim("EC-1")]
    [SpecClaim("EC-2")]
    private async Task OnSubmitAsync()
    {
        var customer = State.CustomerName?.Trim() ?? "";

        // INV-1 + EC-1: empty customer short-circuits with no external state.
        if (customer.Length == 0)
        {
            State.Status = "Customer required";
            return;
        }

        State.IsSubmitting = true;
        try
        {
            // INV-2 + SE-1: exactly-once insert. RecordsAffected = 1
            // asserted by the repository before the export runs.
            await Orders.InsertAsync(new Order { Id = State.OrderId, CustomerName = customer });
        }
        catch (DbException ex) when (IsKnownDaoError(ex))
        {
            // OE-1: DBErrHandler equivalent. Set Status; do NOT export.
            State.Status = ex.Message;
            State.IsSubmitting = false;
            return;
        }

        await Exporter.ExportInvoiceAsync(State.OrderId);
        State.Status = "Submitted";
        State.IsSubmitting = false;
    }

    private static bool IsKnownDaoError(DbException ex) =>
        ex.ErrorCode == 3022 || ex.ErrorCode == 3134;
}

// Placeholder for the invoice exporter — implementer wires this up at
// scaffold time. For Blazor Server, ClosedXML output is streamed to the
// browser via a download endpoint or saved to the customer-configured
// shared storage; the choice is an Open Question on the spec.
public sealed class InvoiceExporter
{
    public Task ExportInvoiceAsync(long orderId)
    {
        // TODO: stream the .xlsx to the browser or write to shared storage.
        return Task.CompletedTask;
    }
}
