VERSION 5.00
Begin VB.Form frmCustomerLookup
   Caption         =   "Customer Lookup"
   ClientHeight    =   3615
   ClientWidth     =   6000
   StartUpPosition =   1  'CenterOwner
   Begin VB.TextBox txtSearch
      Height          =   315
      Left            =   240
      TabIndex        =   0
      Top             =   240
      Width           =   4575
   End
   Begin VB.CommandButton btnSearch
      Caption         =   "&Search"
      Default         =   -1
      Height          =   315
      Left            =   4920
      TabIndex        =   1
      Top             =   240
      Width           =   855
   End
   Begin VB.ListBox lstResults
      Height          =   2400
      Left            =   240
      TabIndex        =   2
      Top             =   720
      Width           =   5535
   End
   Begin VB.Label lblCount
      Caption         =   ""
      Height          =   255
      Left            =   240
      TabIndex        =   3
      Top             =   3240
      Width           =   5535
   End
End
Attribute VB_Name = "frmCustomerLookup"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

' frmCustomerLookup — DAO recordset walk that exercises the
' rs.MoveLast / rs.MoveFirst RecordCount idiom (Golden Dataset
' entry vb6-dao-recordcount-movelast-movefirst). Modern EF Core
' port should drop the Move* dance and use CountAsync().

Private dbs As DAO.Database

Private Sub Form_Load()
    Set dbs = OpenSharedDatabase()
End Sub

Private Sub btnSearch_Click()
    Dim rs As DAO.Recordset
    Dim sql As String

    On Error Goto SearchErr

    sql = "SELECT CustomerId, Name, City FROM Customers " & _
          "WHERE Name LIKE " & SqlString("%" & Trim$(txtSearch.Text) & "%") & " " & _
          "ORDER BY Name"
    Set rs = dbs.OpenRecordset(sql)

    lstResults.Clear
    If rs.EOF Then
        lblCount.Caption = "No matches."
        rs.Close
        Exit Sub
    End If

    ' Legacy DAO idiom: MoveLast forces the cursor through every row
    ' so RecordCount returns the true count, NOT the cursor-visited
    ' count. Then MoveFirst resets for the downstream walk.
    rs.MoveLast
    Dim total As Long
    total = rs.RecordCount
    rs.MoveFirst

    Do While Not rs.EOF
        ' DP-1: rs!Name is default-property access.
        lstResults.AddItem rs!Name & "  -  " & rs!City
        lstResults.ItemData(lstResults.NewIndex) = rs!CustomerId
        rs.MoveNext
    Loop

    lblCount.Caption = total & " matching customer(s)"
    rs.Close
    Exit Sub

SearchErr:
    MsgBox "Search failed: " & Err.Description, vbExclamation, "Customer Lookup"
End Sub

Private Sub lstResults_DblClick()
    If lstResults.ListIndex < 0 Then Exit Sub
    SelectedCustomerId = lstResults.ItemData(lstResults.ListIndex)
    Me.Hide
End Sub

Private Sub Form_Unload(Cancel As Integer)
    On Error Resume Next
    If Not dbs Is Nothing Then dbs.Close
    Set dbs = Nothing
End Sub

Private Function SqlString(s As String) As String
    SqlString = "'" & Replace$(s, "'", "''") & "'"
End Function
