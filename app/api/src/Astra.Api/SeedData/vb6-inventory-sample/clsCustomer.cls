VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
  Persistable = 0  'NotPersistable
  DataBindingBehavior = 0  'vbNone
  DataSourceBehavior  = 0  'vbNone
  MTSTransactionMode  = 0  'NotAnMTSObject
END
Attribute VB_Name = "clsCustomer"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = True
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit

' clsCustomer — typed value object for a Customers row. Replaces the
' DAO Recordset Fields("Name").Value access pattern that frmCustomerLookup
' uses today. The headline Set fld = rs!Name vs x = rs!Name trap
' (vb6-default-property-ambiguity Golden Dataset entry) is right here:
' once the recordset is mapped through clsCustomer, the Set vs implicit
' distinction disappears.

Private mId As Long
Private mName As String
Private mCity As String

' PA-1 + PA-2: property accessors. Read-only id, read-write name + city.

Public Property Get CustomerId() As Long
    CustomerId = mId
End Property

Public Property Get Name() As String
    Name = mName
End Property
Public Property Let Name(ByVal value As String)
    mName = value
End Property

Public Property Get City() As String
    City = mCity
End Property
Public Property Let City(ByVal value As String)
    mCity = value
End Property

' Populate from a DAO Recordset positioned on the row to read.
' Reviewers should watch the DP-1 bang-notation access here when
' the LLM extracts the spec — it's the canonical pattern modern EF
' Core would replace with typed property reads.
Public Sub LoadFromRecordset(rs As DAO.Recordset)
    mId = CLng(rs!CustomerId)
    mName = CStr(rs!Name)
    mCity = CStr(rs!City)
End Sub

Public Sub LoadByName(dbs As DAO.Database, customerName As String)
    Dim rs As DAO.Recordset
    Set rs = dbs.OpenRecordset( _
        "SELECT CustomerId, Name, City FROM Customers WHERE Name = '" & _
        Replace$(customerName, "'", "''") & "'")
    If Not rs.EOF Then LoadFromRecordset rs
    rs.Close
End Sub
