VERSION 5.00
Object = "{F9043C88-F6F2-101A-A3C9-08002B2F49FB}#1.2#0"; "comdlg32.ocx"
Begin VB.Form frmOrderEntry
   Caption         =   "Order Entry"
   ClientHeight    =   4815
   ClientLeft      =   60
   ClientTop       =   345
   ClientWidth     =   7440
   LinkTopic       =   "frmOrderEntry"
   StartUpPosition =   1  'CenterOwner
   Begin VB.TextBox txtCustomer
      Height          =   315
      Left            =   1680
      TabIndex        =   1
      Text            =   ""
      Top             =   240
      Width           =   3735
   End
   Begin VB.TextBox txtOrderId
      Height          =   315
      Left            =   1680
      TabIndex        =   2
      Text            =   ""
      Top             =   720
      Width           =   1815
   End
   Begin VB.ComboBox cmbProducts
      Height          =   315
      Left            =   1680
      TabIndex        =   3
      Top             =   1200
      Width           =   3735
   End
   Begin VB.CommandButton btnSubmit
      Caption         =   "&Submit"
      Default         =   -1  'True
      Height          =   495
      Left            =   1680
      TabIndex        =   4
      Top             =   3960
      Width           =   1335
   End
   Begin VB.CommandButton btnCancel
      Cancel          =   -1  'True
      Caption         =   "Cancel"
      Height          =   495
      Left            =   3360
      TabIndex        =   5
      Top             =   3960
      Width           =   1335
   End
   Begin VB.Label lblStatus
      Caption         =   ""
      ForeColor       =   &H00C00000&
      Height          =   495
      Left            =   240
      TabIndex        =   6
      Top             =   4440
      Width           =   7095
   End
   Begin VB.Label lblCustomer
      Caption         =   "Customer:"
      Height          =   255
      Left            =   240
      TabIndex        =   0
      Top             =   240
      Width           =   1335
   End
End
Attribute VB_Name = "frmOrderEntry"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

' frmOrderEntry — headline form for the Astra Phase 10 VB6 demo.
' Calibrated against example-order-submit.json (the comprehensive
' example spec) plus the 6 Golden Dataset traps. Touches every
' VB6-specific claim kind:
'   * on_error_handler   — typed Err.Number filter for DAO 3022 / 3134
'   * com_interop_contract — late-bound Excel.Application via modReports
'   * event_handler_contract — Form_Load + control _Click handlers
'   * default_property_usage — implicit txtCustomer / rs!Field reads
'   * late_binding_call — modReports.ExportInvoiceToExcel dispatch

Private dbsOrders As DAO.Database
Private rsProducts As DAO.Recordset

Private Sub Form_Load()
    ' VB6 GUARANTEES controls are instantiated and their designer-set
    ' properties (Text="", Caption="", etc.) are applied BEFORE this
    ' handler runs. The reads below are safe under VB6; on Blazor the
    ' DOM doesn't exist yet — see example-form-load.json's EH-1.
    On Error Goto LoadFailed

    Set dbsOrders = OpenSharedDatabase()
    Set rsProducts = dbsOrders.OpenRecordset( _
        "SELECT ProductId, Name FROM Products ORDER BY Name")

    PopulateProductsCombo
    txtOrderId.Text = CStr(SuggestNextOrderId(dbsOrders))
    lblStatus.Caption = ""
    Exit Sub

LoadFailed:
    MsgBox "Could not open the orders database: " & Err.Description, _
           vbCritical, "Order Entry"
    Unload Me
End Sub

Private Sub PopulateProductsCombo()
    cmbProducts.Clear
    If rsProducts.EOF Then Exit Sub
    rsProducts.MoveFirst
    Do While Not rsProducts.EOF
        ' DP-1: rs!Name is default-property access (.Fields("Name").Value).
        ' DP-2: rs("ProductId") is the same default property via parens.
        cmbProducts.AddItem rsProducts!Name
        cmbProducts.ItemData(cmbProducts.NewIndex) = rsProducts("ProductId")
        rsProducts.MoveNext
    Loop
    If cmbProducts.ListCount > 0 Then cmbProducts.ListIndex = 0
End Sub

Private Sub btnSubmit_Click()
    Dim customer As String
    Dim orderId As Long

    ' DP-1: txtCustomer in a String context implies .Text.
    customer = Trim$(txtCustomer.Text)

    ' INV-1 + EC-1: empty customer short-circuits with no DAO write,
    ' no Excel call, no audit log entry.
    If Len(customer) = 0 Then
        lblStatus.Caption = "Customer required"
        txtCustomer.SetFocus
        Exit Sub
    End If

    orderId = CLng(Val(txtOrderId.Text))

    ' OE-1: On Error Goto DBErrHandler. The handler narrows to DAO
    ' 3022 (duplicate PK) and 3134 (SQL syntax); other Err.Number
    ' values escape — VB6 default error dialog.
    On Error Goto DBErrHandler

    ' INV-2 + SE-1: exactly-once insert via dbsOrders.Execute. The
    ' RecordsAffected = 1 assertion gates the downstream Excel call.
    dbsOrders.Execute _
        "INSERT INTO Orders(OrderId, CustomerName, ProductId) " & _
        "VALUES(" & orderId & ", " & SqlString(customer) & ", " & _
        cmbProducts.ItemData(cmbProducts.ListIndex) & ")", dbFailOnError

    If dbsOrders.RecordsAffected <> 1 Then
        lblStatus.Caption = "Insert did not affect exactly one row"
        Exit Sub
    End If

    ' CI-1 + LB-1 + SE-2: late-bound Excel export via modReports.
    ' The CreateObject + dispatch chain lives in modReports.bas so
    ' the form keeps the UI logic separate from the COM dispatch.
    If Not modReports.ExportInvoiceToExcel(orderId, ResolveInvoicePath(orderId)) Then
        lblStatus.Caption = "Order saved; invoice export FAILED (see audit log)"
        modAudit.LogEvent "InvoiceExportFailed", "orderId=" & orderId
        Exit Sub
    End If

    lblStatus.Caption = "Submitted (order " & orderId & ")"
    txtCustomer.Text = ""
    txtOrderId.Text = CStr(SuggestNextOrderId(dbsOrders))
    Exit Sub

DBErrHandler:
    Select Case Err.Number
        Case 3022   ' Duplicate primary key
            lblStatus.Caption = "Order id " & orderId & " already exists"
        Case 3134   ' SQL syntax
            lblStatus.Caption = "Internal SQL error — see audit log"
            modAudit.LogEvent "OrderSqlError", Err.Description
        Case Else
            ' Other errors escape — surface as the VB6 default dialog.
            Resume Next
    End Select
End Sub

Private Sub btnCancel_Click()
    Unload Me
End Sub

Private Sub Form_Unload(Cancel As Integer)
    On Error Resume Next
    If Not rsProducts Is Nothing Then rsProducts.Close
    If Not dbsOrders Is Nothing Then dbsOrders.Close
    Set rsProducts = Nothing
    Set dbsOrders = Nothing
End Sub

Private Function ResolveInvoicePath(orderId As Long) As String
    ' Q-1 (open question on spec): hard-coded template path or
    ' config-driven? The signed spec defers; until the SME decides,
    ' we keep the legacy behaviour.
    ResolveInvoicePath = App.Path & "\Invoices\Invoice-" & orderId & ".xls"
End Function

Private Function SqlString(s As String) As String
    SqlString = "'" & Replace$(s, "'", "''") & "'"
End Function
