Attribute VB_Name = "modOrders"
Option Explicit

' modOrders — order-domain helpers used by frmOrderEntry.
' Headline routine UpdateOrderTotal walks an OrderLines recordset
' and writes the running total back to the parent Orders row.
' Calibrated against example-recordset-update.json + the
' vb6-dao-recordcount-movelast-movefirst Golden Dataset entry.

' Suggests the next available order id by querying MAX(OrderId) + 1.
' Replaces the legacy global counter Form_Load used to read from
' a settings file.
Public Function SuggestNextOrderId(dbs As DAO.Database) As Long
    Dim rs As DAO.Recordset
    On Error Goto SuggestErr
    Set rs = dbs.OpenRecordset("SELECT IIF(IsNull(MAX(OrderId)), 0, MAX(OrderId)) + 1 AS NextId FROM Orders")
    SuggestNextOrderId = CLng(rs.Fields(0).Value)
    rs.Close
    Exit Function
SuggestErr:
    ' Bare fallback so the form still opens if the table is empty
    ' or the query plan changes shape on a future Access version.
    SuggestNextOrderId = 1001
End Function

' Recompute Orders.Total as the sum of OrderLines.Total for the
' given order id. Demonstrates default-property access (rs!Total,
' rs("Total")) and late-bound recordset navigation.
Public Sub UpdateOrderTotal(dbs As DAO.Database, orderId As Long)
    Dim rsLines As DAO.Recordset
    Dim rsOrder As DAO.Recordset
    Dim total As Currency

    ' INV-2: Currency precision preserved end-to-end (no Double cast).
    total = 0@

    Set rsLines = dbs.OpenRecordset( _
        "SELECT Total FROM OrderLines WHERE OrderId = " & orderId)
    If Not rsLines.EOF Then
        rsLines.MoveFirst
        Do While Not rsLines.EOF
            ' DP-1: rsLines!Total is default-property access
            ' (.Fields("Total").Value). Currency type, not Variant.
            total = total + rsLines!Total
            rsLines.MoveNext
        Loop
    End If
    rsLines.Close

    Set rsOrder = dbs.OpenRecordset( _
        "SELECT Total FROM Orders WHERE OrderId = " & orderId, _
        dbOpenDynaset)
    If rsOrder.EOF Then
        rsOrder.Close
        Exit Sub
    End If
    rsOrder.Edit
    ' DP-2: rsOrder("Total") via parens is the same default-property
    ' access as bang notation; both paths read/write .Value.
    rsOrder("Total") = total
    rsOrder.Update
    rsOrder.Close
End Sub

' Insert an order header. Wraps dbs.Execute with the
' dbFailOnError flag so duplicate-key collisions raise Err 3022
' and the form's typed DBErrHandler catches them.
Public Sub InsertOrderHeader(dbs As DAO.Database, _
                              orderId As Long, _
                              customer As String, _
                              productId As Long)
    dbs.Execute _
        "INSERT INTO Orders(OrderId, CustomerName, ProductId) " & _
        "VALUES(" & orderId & ", '" & Replace$(customer, "'", "''") & "', " & _
        productId & ")", dbFailOnError
End Sub
