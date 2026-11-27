// SHAPE EXEMPLAR — never shipped in a generated package.
//
// The logic of a converted VB6 form (View_frmRates.razor is the page that draws it). The VB6 code it mirrors:
//
//   40  Attribute VB_Name = "frmRates"
//   41  Option Explicit
//   42  Private maRates() As RateRec
//   43
//   44  Private Sub Form_Load()
//   45      Dim i As Integer
//   46      For i = Year(Now) - 1 To Year(Now) + 1
//   47          cboYear.AddItem CStr(i)
//   48      Next i
//   49      cboYear.ListIndex = 1
//   50      grdRates.Cols = 2: grdRates.Rows = 1
//   51      grdRates.TextMatrix(0, 0) = "Code": grdRates.TextMatrix(0, 1) = "Amount"
//   52  End Sub
//   53
//   54  Private Sub cmdLoad_Click()
//   55      Dim n As Integer, i As Integer
//   56      n = LoadRates(txtFile.Text, maRates)
//   57      If n <= 0 Then Exit Sub
//   58      grdRates.Rows = n + 1
//   59      For i = 1 To n
//   60          grdRates.TextMatrix(i, 0) = maRates(i).Code
//   61          grdRates.TextMatrix(i, 1) = Format$(maRates(i).Amount, "0.00")
//   62      Next i
//   63  End Sub
//   64
//   65  Private Sub cboYear_Click(): txtFile.Text = "RATES" & cboYear.Text & ".DAT": End Sub
//   66  Private Sub grdRates_Click(): End Sub
//   67  Private Sub cmdClose_Click(): Unload Me: End Sub
//
// Conventions (in addition to those in modRates.cs):
//   1. `public partial class <VB_Name> : VbForm` in namespace Faithful.Vb6App, file src/<VB_Name>.cs — a plain class
//      (no Blazor), so the signed-spec tests drive the form the way a user would: set controls, call handlers.
//   2. One field per control, named exactly as the control, of the matching Faithful.Vb6 type (VbLabel, VbTextBox,
//      VbComboBox, VbListBox, VbCheckBox, VbOptionButton, VbCommandButton, VbFrame, VbFlexGrid for VSFlexGrid and
//      MSFlexGrid, VbPictureBox, VbTimer, VbProgressBar; VbControl for anything else). Its design-time properties
//      from the .frm (Caption, Text, Left/Top/Width/Height, Enabled, Visible, Style, ListIndex) are set in the field
//      initialiser. A control array (Index = n in the .frm) is a VbControlArray<T>. The form's own Caption/size are
//      set in the constructor.
//   3. Event handlers keep their VB6 names (cmdLoad_Click, cboYear_Click, Form_Load) and become `public` so the
//      page and the signed-spec tests can call them (Form_Load/Form_Unload override VbForm's, so they stay protected).
//   4. VB6's indexed properties use indexers: TextMatrix(r, c) → TextMatrix[r, c], List(i) → List[i].
//   5. `Unload Me` → Unload(); `Me.Hide` → Hide(); `frmOther.Show vbModal` → Forms.Get<frmOther>().Show(Vb.vbModal).
namespace Faithful.Vb6App;

using Faithful.Provenance;
using Faithful.Vb6;
using Microsoft.VisualBasic;
using static Faithful.Vb6App.modRates;

[SourceUnit("Forms/frmRates.frm")]
public partial class frmRates : VbForm
{
    public VbLabel lblYear = new() { Name = "lblYear", Caption = "Year", Left = 120, Top = 120, Width = 600, Height = 255 };
    public VbComboBox cboYear = new() { Name = "cboYear", Style = 2, Left = 840, Top = 120, Width = 1200, Height = 315 };
    public VbTextBox txtFile = new() { Name = "txtFile", Left = 2160, Top = 120, Width = 2000, Height = 315 };
    public VbFlexGrid grdRates = new() { Name = "grdRates", Left = 120, Top = 600, Width = 5760, Height = 2800 };
    public VbCommandButton cmdLoad = new() { Name = "cmdLoad", Caption = "&Load", Left = 3480, Top = 3600, Width = 1200, Height = 375 };
    public VbCommandButton cmdClose = new() { Name = "cmdClose", Caption = "Close", Left = 4680, Top = 3600, Width = 1200, Height = 375 };

    private RateRec[] maRates = Array.Empty<RateRec>();

    public frmRates()
    {
        Caption = "Rates";
        Width = 6000;
        Height = 4200;
    }

    [SourceRoutine("Forms/frmRates.frm", "Form_Load", 44, 52)]
    protected override void Form_Load()
    {
        // TODO(faithful): no signed spec — behaviour taken from source only
        short i;
        for (i = (short)(DateAndTime.Year(DateAndTime.Now) - 1); i <= DateAndTime.Year(DateAndTime.Now) + 1; i++)
        {
            cboYear.AddItem(Conversion.Str(i).Trim());   // CStr(i)
        }
        cboYear.ListIndex = 1;
        grdRates.Cols = 2;
        grdRates.Rows = 1;
        grdRates.TextMatrix[0, 0] = "Code";
        grdRates.TextMatrix[0, 1] = "Amount";
    }

    [SourceRoutine("Forms/frmRates.frm", "cmdLoad_Click", 54, 63)]
    [SpecClaim("SE-1")]
    public void cmdLoad_Click()
    {
        short n;
        short i;
        n = LoadRates(txtFile.Text, ref maRates);
        if (n <= 0) return;   // Exit Sub
        grdRates.Rows = n + 1;
        for (i = 1; i <= n; i++)
        {
            grdRates.TextMatrix[i, 0] = maRates[i].Code;
            grdRates.TextMatrix[i, 1] = Strings.Format(maRates[i].Amount, "0.00");
        }
    }

    [SourceRoutine("Forms/frmRates.frm", "cboYear_Click", 65, 65)]
    public void cboYear_Click()
    {
        // TODO(faithful): no signed spec — behaviour taken from source only
        txtFile.Text = "RATES" + cboYear.Text + ".DAT";
    }

    [SourceRoutine("Forms/frmRates.frm", "grdRates_Click", 66, 66)]
    public void grdRates_Click()
    {
        // TODO(faithful): no signed spec — behaviour taken from source only
    }

    [SourceRoutine("Forms/frmRates.frm", "cmdClose_Click", 67, 67)]
    public void cmdClose_Click()
    {
        // TODO(faithful): no signed spec — behaviour taken from source only
        Unload();   // Unload Me
    }
}
