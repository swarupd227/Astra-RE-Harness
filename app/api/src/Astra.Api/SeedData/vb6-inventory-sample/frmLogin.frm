VERSION 5.00
Begin VB.Form frmLogin
   Caption         =   "Astra Inventory — Login"
   ClientHeight    =   2400
   ClientWidth     =   4200
   StartUpPosition =   2  'CenterScreen
   Begin VB.TextBox txtUser
      Height          =   315
      Left            =   1320
      TabIndex        =   0
      Top             =   240
      Width           =   2655
   End
   Begin VB.TextBox txtPassword
      Height          =   315
      IMEMode         =   3  'DISABLE
      Left            =   1320
      PasswordChar    =   "*"
      TabIndex        =   1
      Top             =   720
      Width           =   2655
   End
   Begin VB.CommandButton btnSignIn
      Caption         =   "Sign &In"
      Default         =   -1
      Height          =   375
      Left            =   1320
      TabIndex        =   2
      Top             =   1320
      Width           =   1215
   End
   Begin VB.CommandButton btnExit
      Cancel          =   -1
      Caption         =   "E&xit"
      Height          =   375
      Left            =   2760
      TabIndex        =   3
      Top             =   1320
      Width           =   1215
   End
   Begin VB.Label lblStatus
      Caption         =   ""
      Height          =   255
      Left            =   120
      TabIndex        =   4
      Top             =   1920
      Width           =   3975
   End
End
Attribute VB_Name = "frmLogin"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

' frmLogin — entry form. Demonstrates the On Error Resume Next
' anti-pattern (Golden Dataset entry
' vb6-on-error-resume-next-masks-divbyzero): the auth check
' silently swallows database failures and falls through to a
' "user not found" message — masking real outages as bad-creds
' errors.

Private Sub btnSignIn_Click()
    Dim authed As Boolean

    On Error Resume Next   ' <-- anti-pattern; should be Goto.
    authed = modUsers.Authenticate(Trim$(txtUser.Text), txtPassword.Text)
    ' If the database is down, Authenticate raises Err — and we
    ' silently treat that as authed=False below. Real failures
    ' look identical to bad credentials.
    On Error Goto 0

    If authed Then
        modGlobals.CurrentUser = Trim$(txtUser.Text)
        Me.Hide
        frmMainMDI.Show
    Else
        lblStatus.Caption = "Sign-in failed."
        txtPassword.Text = ""
        txtPassword.SetFocus
    End If
End Sub

Private Sub btnExit_Click()
    End
End Sub
