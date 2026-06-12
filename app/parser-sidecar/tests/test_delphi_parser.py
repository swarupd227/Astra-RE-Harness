"""
Smoke tests for the Phase 9.0.a Delphi parser scaffolding.

The reference fixture is a 50-line Delphi unit modelled on TIdSMTP — a
mini SMTP wrapper that exercises every structural feature the v0 parser
is required to handle: unit header, `uses` clause, interface vs
implementation sections, class declaration, several procedures and a
function with calls into one another.

The fpc-AST production parser (ADR-024) MUST produce the same outcome
shape on this fixture; that's the contract between v0 and v1.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.delphi_parser import parse_source


# ──────────────────────────────────────────────────────────────────────
# Fixture — small but representative Delphi unit
# ──────────────────────────────────────────────────────────────────────

_INDY_LIKE = textwrap.dedent("""\
    {*
     * Indy-like SMTP wrapper — minimal fixture for the Phase 9.0.a
     * scaffolding test. Not real Indy code; structurally analogous so
     * the parser exercises every supported construct.
     *}
    unit IdSMTPMin;

    interface

    uses
      Classes, SysUtils, IdBaseComponent;

    type
      TIdSMTPMin = class(TIdBaseComponent)
      private
        FHost: string;
        FPort: Integer;
      public
        constructor Create(AHost: string; APort: Integer);
        procedure Connect;
        procedure Disconnect;
        function SendMessage(const ABody: string): Boolean;
      end;

    implementation

    uses
      IdGlobal;

    constructor TIdSMTPMin.Create(AHost: string; APort: Integer);
    begin
      inherited Create;
      FHost := AHost;
      FPort := APort;
    end;

    procedure TIdSMTPMin.Connect;
    begin
      // open the socket
      OpenSocket(FHost, FPort);
      Handshake;
    end;

    procedure TIdSMTPMin.Disconnect;
    begin
      CloseSocket;
    end;

    function TIdSMTPMin.SendMessage(const ABody: string): Boolean;
    begin
      Result := SendCmd('DATA') and SendCmd(ABody) and SendCmd('.');
    end;

    end.
""")


# ──────────────────────────────────────────────────────────────────────
# Tests
# ──────────────────────────────────────────────────────────────────────


def test_parses_unit_header_and_uses_clauses():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    # Every `uses` clause across both sections lands in the common-block-
    # refs bucket on every routine — see delphi_parser.py for the rationale.
    expected = {"Classes", "SysUtils", "IdBaseComponent", "IdGlobal"}
    for r in out.subroutines:
        assert set(r.common_block_refs) >= expected, (
            f"{r.name} missing uses: got {r.common_block_refs}"
        )


def test_recognises_four_routines():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    names = sorted(r.name for r in out.subroutines)
    # We assert the class-qualified names are recognised verbatim. v0
    # does not auto-qualify (see _class_qualify) so the parser returns
    # `TIdSMTPMin.Create` etc. exactly as they appear in source.
    assert names == [
        "TIdSMTPMin.Connect",
        "TIdSMTPMin.Create",
        "TIdSMTPMin.Disconnect",
        "TIdSMTPMin.SendMessage",
    ]


def test_captures_called_routines_inside_body():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    by_name = {r.name: r for r in out.subroutines}
    # Connect calls OpenSocket and Handshake; Disconnect calls
    # CloseSocket; SendMessage calls SendCmd three times (de-duped
    # to a single entry by the parser).
    assert "OpenSocket" in by_name["TIdSMTPMin.Connect"].called_subroutines
    assert "Handshake" in by_name["TIdSMTPMin.Connect"].called_subroutines
    assert "CloseSocket" in by_name["TIdSMTPMin.Disconnect"].called_subroutines
    sm_calls = by_name["TIdSMTPMin.SendMessage"].called_subroutines
    assert "SendCmd" in sm_calls
    # Dedup invariant.
    assert sm_calls.count("SendCmd") == 1


def test_line_ranges_are_monotonic_and_within_file():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    for r in out.subroutines:
        assert 1 <= r.line_start <= r.line_end <= out.line_count, (
            f"{r.name} line range {r.line_start}-{r.line_end} outside file (lines={out.line_count})"
        )


def test_empty_file_does_not_crash():
    out = parse_source("Empty.pas", "")
    assert out.subroutines == []
    assert out.line_count == 0


def test_file_without_header_emits_warning_but_parses():
    body_only = textwrap.dedent("""\
        procedure HelperA;
        begin
          HelperB;
        end;

        procedure HelperB;
        begin
        end;
    """)
    out = parse_source("snippet.inc", body_only)
    assert any("no unit/program/library header" in w for w in out.warnings)
    names = sorted(r.name for r in out.subroutines)
    assert names == ["HelperA", "HelperB"]
    # HelperA's body calls HelperB.
    helper_a = next(r for r in out.subroutines if r.name == "HelperA")
    assert "HelperB" in helper_a.called_subroutines


def test_brace_comments_dont_corrupt_call_detection():
    """A heuristic call-detector that doesn't strip comments would
    incorrectly flag `SomeRoutine` inside a comment as a call."""
    src = textwrap.dedent("""\
        unit X;
        interface
        procedure A;
        implementation
        procedure A;
        begin
          { do not call SomeRoutine(here) anymore }
          // SomeRoutine
          (* SomeRoutine *)
          ActuallyCalled(1, 2);
        end;
        end.
    """)
    out = parse_source("X.pas", src)
    a = next(r for r in out.subroutines if r.name == "A")
    assert "ActuallyCalled" in a.called_subroutines
    assert "SomeRoutine" not in a.called_subroutines
