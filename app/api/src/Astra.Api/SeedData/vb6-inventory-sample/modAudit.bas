Attribute VB_Name = "modAudit"
Option Explicit

' modAudit — thin wrapper around modGlobals.LogAudit so the rest
' of the codebase calls modAudit.LogEvent (the conventional name).
' Provided as a separate module so the headline forms touch a
' clean audit surface that's easy to refactor.

Public Sub LogEvent(eventType As String, payload As String)
    modGlobals.LogAudit eventType, payload
End Sub
