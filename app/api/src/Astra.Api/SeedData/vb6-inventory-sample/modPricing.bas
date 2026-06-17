Attribute VB_Name = "modPricing"
Option Explicit

' modPricing — discount + line-format helpers used by frmOrderEntry
' and the invoice exporter. Three small routines that together
' exercise three Golden Dataset traps:
'   * SafeAverage uses On Error Resume Next to swallow div-by-zero
'     (vb6-on-error-resume-next-masks-divbyzero)
'   * FormatLineLabel uses both + (numeric add) and & (string concat)
'     in close proximity (vb6-variant-plus-vs-amp-coercion)
'   * ApplyDiscount accepts a Variant discount of unclear runtime type

Public Function ApplyDiscount(basePrice As Currency, _
                              ByVal discount As Variant) As Currency
    ' INV-1: return value never exceeds basePrice when discount >= 0.
    ' EC-3 (covered by spec): if discount is non-numeric String or Null,
    ' returns basePrice unchanged.
    Dim pct As Double
    On Error Resume Next
    pct = CDbl(discount)
    If Err.Number <> 0 Then
        ApplyDiscount = basePrice
        Err.Clear
        Exit Function
    End If
    On Error Goto 0

    If pct < 0 Then pct = 0
    If pct > 100 Then pct = 100
    ApplyDiscount = basePrice * (1@ - CCur(pct) / 100@)
End Function

' Computes the average of total / divisor where divisor may be 0.
' Trap: On Error Resume Next swallows the div-by-zero AND any
' other Err.Number — overflow, type-mismatch, etc.
Public Function SafeAverage(total As Currency, divisor As Long) As Currency
    Dim result As Currency
    On Error Resume Next
    result = total / divisor
    SafeAverage = result
End Function

' Format a one-line invoice label. Uses & for string concat — the
' implementer-translated C# port that uses + would silently produce
' different output for mixed-type Variants (Golden Dataset entry
' vb6-variant-plus-vs-amp-coercion).
Public Function FormatLineLabel(qty As Long, sku As String, _
                                 ByVal discount As Variant) As String
    Dim label As String
    label = qty & "x " & sku
    If IsNumeric(discount) Then
        label = label & " -" & CStr(discount) & "%"
    End If
    FormatLineLabel = label
End Function
