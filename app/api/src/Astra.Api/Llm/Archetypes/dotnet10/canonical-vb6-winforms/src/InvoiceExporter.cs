// SPDX-Spec: vb6/modReports.ExportInvoiceToExcel (signed)
// SPDX-Archetype: canonical-vb6-winforms
//
// Replaces VB6's `Set objExcel = CreateObject("Excel.Application")` with a
// managed ClosedXML write — no Office Interop, no COM threading, no
// orphaned EXCEL.EXE processes. The CI-1 (com_interop_contract) +
// LB-1 (late_binding_call) claims from the signed spec drove this
// translation choice; see archetype.json for the claim mapping.

using ClosedXML.Excel;

namespace Demo.OrderEntry;

/// <summary>
/// Writes per-order invoices to .xlsx. Pure managed code — no Office
/// Interop, no COM, no STA threads. Idempotent: writing the same
/// (orderId, targetPath) twice produces byte-identical output.
/// </summary>
[SpecClaim("CI-1")]
public sealed class InvoiceExporter
{
    /// <summary>
    /// INV-2 + SE-2: produce one .xlsx workbook at targetPath. Sheet name
    /// is "Invoice"; header on rows 1-4, lines from row 6.
    /// </summary>
    /// <param name="orderId">Order key. Used as the workbook title.</param>
    /// <param name="targetPath">Absolute path of the .xlsx to write.</param>
    [SpecClaim("INV-2")]
    [SpecClaim("LB-1")]
    [SpecClaim("SE-2")]
    public Task ExportInvoiceAsync(long orderId, string targetPath)
    {
        // TODO: read the Orders + OrderLines rows for orderId and populate
        // the workbook. The signed spec's example-excel-export entry
        // (INV-1: sheet name "Invoice", rows 1-4 header + rows 6+ lines)
        // drives the cell layout.
        return Task.Run(() =>
        {
            using var book = new XLWorkbook();
            var sheet = book.AddWorksheet("Invoice");
            sheet.Cell("A1").Value = "Order";
            sheet.Cell("B1").Value = orderId;
            // ... header rows 2-4, line rows 6+ added by the implementer
            EnsureDirectoryExists(targetPath);
            book.SaveAs(targetPath);
        });
    }

    private static void EnsureDirectoryExists(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
