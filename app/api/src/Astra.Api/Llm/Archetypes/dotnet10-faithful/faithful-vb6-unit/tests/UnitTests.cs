// Test anchor for faithful 1:1 VB6 packages. The signed-spec test pack the validation gate generates lands in this
// directory and namespace; this file proves the provenance attributes and the VB6 runtime ship and behave as VB6 did.
using Faithful.Provenance;
using Faithful.Vb6;
using Xunit;

namespace Faithful.Tests;

public class UnitTests
{
    [Fact]
    public void Provenance_attributes_are_available_to_converted_code()
    {
        var routine = new SourceRoutineAttribute("Forms/frmMain.frm", "cmdOK_Click", 48, 56);
        Assert.Equal("cmdOK_Click", routine.Routine);
        Assert.True(routine.LineEnd >= routine.LineStart);
        Assert.Equal("INV-1", new SpecClaimAttribute("INV-1").Id);
    }

    [Fact]
    public void A_list_keeps_vb6_indexes_and_selection()
    {
        var cbo = new VbComboBox { Style = 2 };
        cbo.AddItem("B");
        cbo.AddItem("A", 0);
        Assert.Equal(2, cbo.ListCount);
        Assert.Equal("A", cbo.List[0]);
        Assert.Equal(-1, cbo.ListIndex);
        cbo.Text = "B";
        Assert.Equal(1, cbo.ListIndex);
        cbo.ItemData[1] = 42;
        cbo.RemoveItem(0);
        Assert.Equal(42, cbo.ItemData[0]);
    }

    [Fact]
    public void A_grid_is_addressed_by_text_matrix()
    {
        var grd = new VbFlexGrid { Cols = 3, Rows = 1 };
        grd.TextMatrix[0, 2] = "Total";
        grd.AddItem("a\tb\tc");
        Assert.Equal(2, grd.Rows);
        Assert.Equal("c", grd.TextMatrix[1, 2]);
        grd.Row = 1; grd.Col = 0;
        Assert.Equal("a", grd.Text);
    }

    [Fact]
    public void A_message_box_presses_its_default_button_unless_an_answer_is_queued()
    {
        var dialogs = new RecordingDialogs();
        App.Dialogs = dialogs;
        Assert.Equal(VbMsgBoxResult.vbYes, Vb.MsgBox("Save?", VbMsgBoxStyle.vbYesNo));
        Assert.Equal(VbMsgBoxResult.vbNo, Vb.MsgBox("Save?", VbMsgBoxStyle.vbYesNo | VbMsgBoxStyle.vbDefaultButton2));
        dialogs.Answers.Enqueue(VbMsgBoxResult.vbNo);
        Assert.Equal(VbMsgBoxResult.vbNo, Vb.MsgBox("Save?", VbMsgBoxStyle.vbYesNo));
        Assert.Equal(3, dialogs.Messages.Count);
    }

    [Fact]
    public void Err_reports_vb6_error_numbers()
    {
        try { Err.Raise(1001, "test", "custom"); }
        catch (Exception ex) { Err.Set(ex); }
        Assert.Equal(1001, Err.Number);
        Assert.Equal("custom", Err.Description);
        Err.Set(new DivideByZeroException());
        Assert.Equal(11, Err.Number);
        Err.Clear();
        Assert.Equal(0, Err.Number);
    }
}
