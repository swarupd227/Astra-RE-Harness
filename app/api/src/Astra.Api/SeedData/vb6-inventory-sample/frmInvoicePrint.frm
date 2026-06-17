VERSION 5.00
Begin VB.Form frmInvoicePrint
   Caption         =   "Print Invoice"
   ClientHeight    =   2880
   ClientWidth     =   5040
   StartUpPosition =   1  'CenterOwner
   Begin VB.TextBox txtOrderId
      Height          =   315
      Left            =   1440
      TabIndex        =   0
      Top             =   240
      Width           =   1815
   End
   Begin VB.CheckBox chkOpenAfter
      Caption         =   "Open in Excel after writing"
      Height          =   255
      Left            =   240
      TabIndex        =   1
      Top             =   720
      Value           =   1
      Width           =   3375
   End
   Begin VB.CommandButton btnExport
      Caption         =   "&Export"
      Default         =   -1
      Height          =   375
      Left            =   1440
      TabIndex        =   2
      Top             =   1440
      Width           =   1095
   End
   Begin VB.Label lblStatus
      Caption         =   ""
      Height          =   495
      Left            =   240
      TabIndex        =   3
      Top             =   2040
      Width           =   4575
   End
End
Attribute VB_Name = "frmInvoicePrint"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

' frmInvoicePrint — minimal launcher for the Excel export path.
' Calls modReports.ExportInvoiceToExcel and optionally opens the
' resulting .xls in the visible Excel UI (separate CreateObject
' call from the headless export).

Private Sub btnExport_Click()
    Dim orderId As Long
    Dim target As String
    Dim ok As Boolean

    orderId = CLng(Val(txtOrderId.Text))
    If orderId <= 0 Then
        lblStatus.Caption = "Enter a valid order id."
        Exit Sub
    End If

    target = App.Path & "\Invoices\Invoice-" & orderId & ".xls"
    ok = modReports.ExportInvoiceToExcel(orderId, target)
    If Not ok Then
        lblStatus.Caption = "Export failed; see audit log."
        Exit Sub
    End If

    lblStatus.Caption = "Wrote " & target
    If chkOpenAfter.Value = vbChecked Then OpenWorkbookInVisibleExcel target
End Sub

Private Sub OpenWorkbookInVisibleExcel(path As String)
    ' Second CreateObject site — DIFFERENT instance than the
    ' headless one in modReports.ExportInvoiceToExcel. The user
    ' sees this Excel; the export one stays hidden.
    On Error Goto OpenErr
    Dim xl As Object
    Set xl = CreateObject("Excel.Application")
    xl.Visible = True
    xl.Workbooks.Open path
    Exit Sub

OpenErr:
    lblStatus.Caption = "Could not open Excel: " & Err.Description
End Sub
