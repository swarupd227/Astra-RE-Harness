"""
Tests for parser_sidecar.vb6_parser.

Phase 10.0.a — smoke coverage for the v0 tokenizer. Asserts the parser
extracts routine declarations, COM call sites, On Error warnings, and
.frm property-bag handling — the structural surface the Migration
Planner and Spec extractor depend on.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.vb6_parser import (
    parse_source,
    parse_frm_layout,
    SubroutineSummary,
)


# ──────────────────────────────────────────────────────────────────────
# Basic Sub / Function recognition
# ──────────────────────────────────────────────────────────────────────


def test_parses_public_sub_with_params():
    src = textwrap.dedent("""
        Attribute VB_Name = "OrderEntry"
        Option Explicit

        Public Sub Submit(orderId As Long, customerName As String)
            Debug.Print orderId
        End Sub
    """).strip()

    out = parse_source(filename="OrderEntry.bas", content=src)

    assert out.line_count == src.count("\n") + 1
    assert len(out.subroutines) == 1
    sub = out.subroutines[0]
    assert sub.name == "Submit"
    assert "Public Sub Submit" in sub.signature
    assert sub.line_start > 0 and sub.line_end > sub.line_start
    # Module name should appear in common_block_refs (cross-routine
    # dependency surface).
    assert "OrderEntry" in sub.common_block_refs


def test_parses_function_with_return_type():
    src = textwrap.dedent("""
        Attribute VB_Name = "Util"
        Public Function ComputeTotal(qty As Long, price As Currency) As Currency
            ComputeTotal = qty * price
        End Function
    """).strip()

    out = parse_source(filename="Util.bas", content=src)

    assert len(out.subroutines) == 1
    fn = out.subroutines[0]
    assert fn.name == "ComputeTotal"
    assert "Function ComputeTotal" in fn.signature
    assert "As Currency" in fn.signature


def test_parses_property_accessors():
    src = textwrap.dedent("""
        Attribute VB_Name = "Customer"
        Private mName As String

        Public Property Get Name() As String
            Name = mName
        End Property

        Public Property Let Name(ByVal value As String)
            mName = value
        End Property
    """).strip()

    out = parse_source(filename="Customer.cls", content=src)

    assert len(out.subroutines) == 2
    names = {s.name for s in out.subroutines}
    assert names == {"Name (Get)", "Name (Let)"}


# ──────────────────────────────────────────────────────────────────────
# On Error handler surfacing
# ──────────────────────────────────────────────────────────────────────


def test_on_error_resume_next_surfaces_as_warning():
    src = textwrap.dedent("""
        Attribute VB_Name = "Net"
        Public Sub PostInvoice()
            On Error Resume Next
            Dim x As Long
            x = 1 / 0
        End Sub
    """).strip()

    out = parse_source(filename="Net.bas", content=src)

    # Routine still parsed.
    assert len(out.subroutines) == 1
    # On Error Resume Next emits an on_error_handler advisory.
    matching = [w for w in out.warnings if "On Error Resume Next" in w]
    assert len(matching) == 1
    assert "PostInvoice" in matching[0]


def test_on_error_goto_surfaces_as_warning():
    src = textwrap.dedent("""
        Attribute VB_Name = "Net"
        Public Sub LoadFile()
            On Error Goto Handler
            Open "x.txt" For Input As #1
            Exit Sub
        Handler:
            Resume Next
        End Sub
    """).strip()

    out = parse_source(filename="Net.bas", content=src)

    matching = [w for w in out.warnings if "On Error Goto" in w]
    assert len(matching) == 1


# ──────────────────────────────────────────────────────────────────────
# COM interop detection
# ──────────────────────────────────────────────────────────────────────


def test_create_object_recorded_with_com_prefix():
    src = textwrap.dedent("""
        Attribute VB_Name = "Excel"
        Public Sub ExportInvoice()
            Dim app As Object
            Set app = CreateObject("Excel.Application")
            app.Visible = True
        End Sub
    """).strip()

    out = parse_source(filename="Excel.bas", content=src)

    assert len(out.subroutines) == 1
    sub = out.subroutines[0]
    assert "com:Excel.Application" in sub.called_subroutines


def test_get_object_recorded_with_com_prefix():
    src = textwrap.dedent("""
        Attribute VB_Name = "FileHelpers"
        Public Sub ReadFile()
            Dim fs As Object
            Set fs = GetObject(, "Scripting.FileSystemObject")
        End Sub
    """).strip()

    out = parse_source(filename="FileHelpers.bas", content=src)

    assert "com:<GetObject>" in out.subroutines[0].called_subroutines


# ──────────────────────────────────────────────────────────────────────
# Win32 P/Invoke `Declare` recognition
# ──────────────────────────────────────────────────────────────────────


def test_declare_pinvoke_emits_warning():
    src = textwrap.dedent("""
        Attribute VB_Name = "Win32"
        Public Declare Function GetTickCount Lib "kernel32" () As Long
        Public Declare Sub Sleep Lib "kernel32" (ByVal ms As Long)

        Public Sub Wait(ms As Long)
            Sleep ms
        End Sub
    """).strip()

    out = parse_source(filename="Win32.bas", content=src)

    # Routine count is just the Sub Wait — Declare lines do NOT become
    # subroutines (they're external bindings).
    assert len(out.subroutines) == 1
    assert out.subroutines[0].name == "Wait"
    # The Declares surface as a warning naming the Lib bindings.
    assert any("P/Invoke" in w for w in out.warnings)
    assert any("kernel32" in w for w in out.warnings)


# ──────────────────────────────────────────────────────────────────────
# .frm property-bag parser
# ──────────────────────────────────────────────────────────────────────


def test_frm_property_bag_extracts_form_and_controls():
    src = textwrap.dedent("""
        VERSION 5.00
        Begin VB.Form frmOrder
           Caption         =   "Order Entry"
           ClientHeight    =   3015
           Begin VB.CommandButton btnSubmit
              Caption         =   "Submit"
              Height          =   375
           End
           Begin VB.TextBox txtCustomer
              Height          =   375
           End
        End
        Attribute VB_Name = "frmOrder"
        Attribute VB_GlobalNameSpace = False

        Private Sub btnSubmit_Click()
            MsgBox "submitted"
        End Sub
    """).strip()

    layout = parse_frm_layout(src)

    assert layout.root is not None
    assert layout.root.kind == "VB.Form"
    assert layout.root.name == "frmOrder"
    assert layout.root.properties.get("Caption") == '"Order Entry"'

    # Two children: btnSubmit (CommandButton) + txtCustomer (TextBox)
    assert len(layout.root.children) == 2
    kinds = {c.kind for c in layout.root.children}
    assert kinds == {"VB.CommandButton", "VB.TextBox"}

    # Attribute block at the bottom is captured.
    assert layout.attributes.get("VB_Name") == "frmOrder"


def test_frm_code_block_parses_event_handler():
    """For .frm files, parse_source should skip past the property bag
    and parse only the code block underneath."""
    src = textwrap.dedent("""
        VERSION 5.00
        Begin VB.Form frmOrder
           Caption         =   "Order Entry"
        End
        Attribute VB_Name = "frmOrder"
        Attribute VB_GlobalNameSpace = False

        Private Sub btnSubmit_Click()
            MsgBox "submitted"
        End Sub
    """).strip()

    out = parse_source(filename="frmOrder.frm", content=src)

    # Just the event handler should appear as a routine — the property
    # bag must NOT be misread as code.
    assert len(out.subroutines) == 1
    assert out.subroutines[0].name == "btnSubmit_Click"


# ──────────────────────────────────────────────────────────────────────
# Robustness: comment stripping, line ranges
# ──────────────────────────────────────────────────────────────────────


def test_apostrophe_comments_are_stripped():
    src = textwrap.dedent("""
        Attribute VB_Name = "X"
        Public Sub Foo()  ' This is a comment after the declaration
            Debug.Print "hi"  ' inline comment
            ' standalone comment
        End Sub  ' trailing comment on End
    """).strip()

    out = parse_source(filename="X.bas", content=src)

    assert len(out.subroutines) == 1
    assert out.subroutines[0].name == "Foo"


def test_multiple_routines_track_line_ranges():
    src = textwrap.dedent("""
        Attribute VB_Name = "Two"

        Public Sub First()
            Debug.Print "a"
        End Sub

        Public Sub Second()
            Debug.Print "b"
        End Sub
    """).strip()

    out = parse_source(filename="Two.bas", content=src)

    assert len(out.subroutines) == 2
    first, second = out.subroutines
    assert first.line_end < second.line_start
    # Ranges should be non-overlapping.
    assert first.line_end < second.line_start
