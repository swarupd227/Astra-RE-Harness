Attribute VB_Name = "modGlobals"
Option Explicit

' modGlobals — shared constants, the per-process current-user
' identifier, and the OpenSharedDatabase / Authenticate helpers.

' Per-process current user. Set by frmLogin after a successful
' Authenticate call.
Public CurrentUser As String

' Per-process selected customer id. Set by frmCustomerLookup.
Public SelectedCustomerId As Long

' Application-relative path to the JET .mdb. Resolved at runtime
' via App.Path because the install location varies per site.
Public Function DatabasePath() As String
    DatabasePath = App.Path & "\Data\Inventory.mdb"
End Function

' Open the shared JET .mdb. Cached for the process lifetime so
' every form gets the same handle.
Public Function OpenSharedDatabase() As DAO.Database
    Static cached As DAO.Database
    If cached Is Nothing Then Set cached = DAO.DBEngine.OpenDatabase(DatabasePath())
    Set OpenSharedDatabase = cached
End Function

' Stamp a row into the AuditLog table. Best-effort — used by
' OE-1 handlers in frmOrderEntry + modReports.
Public Sub LogAudit(eventType As String, payload As String)
    On Error Resume Next   ' best-effort; audit must not crash the caller
    Dim dbs As DAO.Database
    Set dbs = OpenSharedDatabase()
    dbs.Execute _
        "INSERT INTO AuditLog(EventType, Payload, OccurredAt, UserName) " & _
        "VALUES('" & Replace$(eventType, "'", "''") & "', '" & _
        Replace$(payload, "'", "''") & "', Now(), '" & _
        Replace$(CurrentUser, "'", "''") & "')"
End Sub
