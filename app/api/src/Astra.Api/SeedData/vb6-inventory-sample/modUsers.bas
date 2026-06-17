Attribute VB_Name = "modUsers"
Option Explicit

' modUsers — minimal credential check. Production code would
' use a salted hash; for the demo we keep the algorithm visible
' so reviewers can see what claims the LLM extracts.

Public Function Authenticate(userName As String, password As String) As Boolean
    Dim dbs As DAO.Database
    Dim rs As DAO.Recordset

    Set dbs = OpenSharedDatabase()
    Set rs = dbs.OpenRecordset( _
        "SELECT PasswordHash FROM Users WHERE UserName = '" & _
        Replace$(userName, "'", "''") & "'")

    If rs.EOF Then
        rs.Close
        Authenticate = False
        Exit Function
    End If

    ' Implementer note: real production code MUST replace this with a
    ' typed Argon2 / PBKDF2 / scrypt verify. The plaintext compare
    ' below is for demo calibration ONLY.
    Authenticate = (rs!PasswordHash = WeakHash(password))
    rs.Close
End Function

Private Function WeakHash(s As String) As String
    Dim i As Long, acc As Long
    For i = 1 To Len(s)
        acc = (acc * 31 + Asc(Mid$(s, i, 1))) And &H7FFFFFFF
    Next i
    WeakHash = Hex$(acc)
End Function
