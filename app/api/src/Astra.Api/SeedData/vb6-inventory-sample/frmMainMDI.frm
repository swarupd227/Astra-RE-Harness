VERSION 5.00
Begin VB.MDIForm frmMainMDI
   BackColor       =   &H8000000C&
   Caption         =   "Astra Inventory"
   ClientHeight    =   6630
   ClientLeft      =   165
   ClientTop       =   735
   ClientWidth     =   9600
   StartUpPosition =   3  'Windows Default
   Begin VB.Menu mnuFile
      Caption         =   "&File"
      Begin VB.Menu mnuFileNewOrder
         Caption         =   "&New Order..."
      End
      Begin VB.Menu mnuFileCustomerLookup
         Caption         =   "&Customer Lookup..."
      End
      Begin VB.Menu mnuFileSep1
         Caption         =   "-"
      End
      Begin VB.Menu mnuFileExit
         Caption         =   "E&xit"
      End
   End
   Begin VB.Menu mnuTools
      Caption         =   "&Tools"
      Begin VB.Menu mnuToolsReports
         Caption         =   "&Reports..."
      End
   End
End
Attribute VB_Name = "frmMainMDI"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

' frmMainMDI — MDI parent form. Menu commands launch the child
' forms; child forms are non-modal so the user can keep multiple
' Order Entry dialogs open at once.

Private Sub MDIForm_Load()
    Me.Caption = "Astra Inventory — " & modGlobals.CurrentUser
End Sub

Private Sub mnuFileNewOrder_Click()
    Dim f As frmOrderEntry
    Set f = New frmOrderEntry
    f.Show vbModal, Me
    Unload f
End Sub

Private Sub mnuFileCustomerLookup_Click()
    frmCustomerLookup.Show vbModal, Me
End Sub

Private Sub mnuFileExit_Click()
    Unload Me
End Sub

Private Sub mnuToolsReports_Click()
    MsgBox "Reports dispatcher is in progress.", vbInformation
End Sub
