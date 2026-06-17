Attribute VB_Name = "modReports"
Option Explicit

' modReports — Excel export routines via late-bound COM.
' Calibrated against example-excel-export.json. The headless export
' (ExportInvoiceToExcel) uses xlApp.Visible = False so the user
' never sees the Excel UI; frmInvoicePrint's "open after" path uses
' a SEPARATE Visible=True instance.

' Export the given order id's invoice to a .xls workbook at
' targetPath. Returns True on success; False on any failure.
' Late-bound throughout — the customer's Excel install may be
' missing (Err 429); the OE-1 handler returns False gracefully.
Public Function ExportInvoiceToExcel(orderId As Long, _
                                      targetPath As String) As Boolean
    Dim xlApp As Object
    Dim xlBook As Object
    Dim xlSheet As Object
    Dim rsLines As DAO.Recordset
    Dim dbs As DAO.Database
    Dim rowIdx As Long

    On Error Goto ExportErr

    ' CI-1 + LB-1: late-bound Excel.Application via CreateObject.
    Set xlApp = CreateObject("Excel.Application")
    xlApp.Visible = False
    Set xlBook = xlApp.Workbooks.Add
    Set xlSheet = xlBook.Worksheets(1)
    xlSheet.Name = "Invoice"

    ' Header rows.
    xlSheet.Cells(1, 1).Value = "Order"
    xlSheet.Cells(1, 2).Value = orderId
    xlSheet.Cells(2, 1).Value = "Generated"
    xlSheet.Cells(2, 2).Value = Now
    xlSheet.Cells(4, 1).Value = "SKU"
    xlSheet.Cells(4, 2).Value = "Qty"
    xlSheet.Cells(4, 3).Value = "Total"

    ' Line rows.
    Set dbs = OpenSharedDatabase()
    Set rsLines = dbs.OpenRecordset( _
        "SELECT Sku, Quantity, Total FROM OrderLines " & _
        "WHERE OrderId = " & orderId & " ORDER BY Id")

    rowIdx = 6
    Do While Not rsLines.EOF
        ' DP-1 + DP-2: bang notation and parens both default to .Value.
        xlSheet.Cells(rowIdx, 1).Value = rsLines!Sku
        xlSheet.Cells(rowIdx, 2).Value = rsLines("Quantity")
        xlSheet.Cells(rowIdx, 3).Value = rsLines!Total
        rowIdx = rowIdx + 1
        rsLines.MoveNext
    Loop

    rsLines.Close
    dbs.Close

    EnsureFolderExists targetPath
    ' xlExcel8 = 56 (Office 2007 constant); legacy .xls.
    xlBook.SaveAs targetPath, 56
    xlBook.Close
    xlApp.Quit

    Set xlSheet = Nothing
    Set xlBook = Nothing
    Set xlApp = Nothing
    ExportInvoiceToExcel = True
    Exit Function

ExportErr:
    ' INV-2 (cleanup): always Quit + release on failure, no orphan
    ' EXCEL.EXE processes.
    On Error Resume Next
    If Not xlBook Is Nothing Then xlBook.Close False
    If Not xlApp Is Nothing Then xlApp.Quit
    Set xlSheet = Nothing
    Set xlBook = Nothing
    Set xlApp = Nothing
    modAudit.LogEvent "ExcelExportFailed", _
        "orderId=" & orderId & " err=" & Err.Number & " desc=" & Err.Description
    ExportInvoiceToExcel = False
End Function

Private Sub EnsureFolderExists(path As String)
    Dim fso As Object
    Dim folder As String
    folder = Left$(path, InStrRev(path, "\") - 1)
    Set fso = CreateObject("Scripting.FileSystemObject")
    If Not fso.FolderExists(folder) Then fso.CreateFolder folder
End Sub
