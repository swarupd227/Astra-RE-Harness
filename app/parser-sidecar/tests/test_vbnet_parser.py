"""
Tests for parser_sidecar.vbnet_parser.

Phase 12.2 — smoke coverage for the VB.NET v0 tokenizer. Asserts the parser
recognises the full VB.NET modifier grammar (Protected/Shared/Async/
Overrides), auto- vs full properties, Class/Module/Namespace containers, and
New/Call reference sites — the structural surface the VB6 tokenizer stub
silently dropped.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.vbnet_parser import parse_source


_SAMPLE = textwrap.dedent("""
    Imports System
    Imports System.Threading.Tasks

    Namespace DDI.Inform.Orders

        Public Module OrderMath
            Public Function LineTotal(ByVal qty As Integer, ByVal price As Decimal) As Decimal
                Return qty * price
            End Function
        End Module

        Public Class OrderService
            Private ReadOnly _repo As IOrderRepository

            Public Sub New(ByVal repo As IOrderRepository)
                _repo = repo
            End Sub

            Public Property CurrentUser As String

            Public ReadOnly Property IsReady As Boolean
                Get
                    Return _repo IsNot Nothing
                End Get
            End Property

            Public Shared Function Create() As OrderService
                Return New OrderService(New SqlOrderRepository())
            End Function

            Public Async Function SubmitAsync(ByVal id As Integer) As Task(Of Boolean)
                Dim ok = Await _repo.SaveAsync(id)
                Return ok
            End Function

            Protected Overrides Sub OnValidate()
                Call LogAudit("validate")
            End Sub

            Private Function LogAudit(ByVal msg As String) As Integer
                Return 0
            End Function
        End Class

    End Namespace
""").strip()


def _by_name(out):
    return {s.name: s for s in out.subroutines}


def test_extracts_all_routines_across_modifiers():
    out = parse_source(filename="OrderService.vb", content=_SAMPLE)
    names = set(_by_name(out))
    # The routines the VB6 stub dropped must now be present.
    assert "Create" in names           # Public Shared Function
    assert "SubmitAsync" in names       # Public Async Function
    assert "OnValidate" in names        # Protected Overrides Sub
    assert "LineTotal" in names         # Function inside a Module
    assert "LogAudit" in names          # Private Function
    assert "New" in names               # constructor


def test_auto_property_is_single_line_and_full_property_has_body():
    out = parse_source(filename="OrderService.vb", content=_SAMPLE)
    props = _by_name(out)
    auto = props["CurrentUser (Property)"]
    full = props["IsReady (Property)"]
    # Auto-property carries no body → start == end.
    assert auto.line_start == auto.line_end
    # Full property walks to End Property → spans multiple lines.
    assert full.line_end > full.line_start


def test_enclosing_container_recorded():
    out = parse_source(filename="OrderService.vb", content=_SAMPLE)
    by = _by_name(out)
    assert by["Create"].common_block_refs == ("OrderService",)
    assert by["LineTotal"].common_block_refs == ("OrderMath",)


def test_new_and_call_sites_collected():
    out = parse_source(filename="OrderService.vb", content=_SAMPLE)
    by = _by_name(out)
    assert any(c.startswith("new:OrderService") for c in by["Create"].called_subroutines)
    assert "LogAudit" in by["OnValidate"].called_subroutines


def test_imports_surfaced_as_warning():
    out = parse_source(filename="OrderService.vb", content=_SAMPLE)
    assert any("imports" in w.lower() for w in out.warnings)


def test_empty_file_is_clean():
    out = parse_source(filename="Empty.vb", content="")
    assert out.subroutines == []
    assert out.line_count == 1
