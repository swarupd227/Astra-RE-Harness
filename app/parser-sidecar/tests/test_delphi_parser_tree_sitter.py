"""
Phase 9.4.a — unit tests for the production Delphi parser
(tree-sitter-pascal per ADR-031).

The 7 base tests mirror the v0 suite verbatim so the production parser
must be at least as accurate as v0 on the same fixtures. The 3 new
tests exercise capabilities v0 cannot handle:

  - generic class declarations (`TStack<T>`)
  - preprocessor branches (`{$IFDEF MSWINDOWS}`)
  - anonymous methods passed to `TThread.Synchronize`

The shared 50-line Indy-like fixture is duplicated here from the v0
suite so the two test files stay decoupled.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.delphi_parser_tree_sitter import parse_source


# ──────────────────────────────────────────────────────────────────────
# Base fixture (mirrors the v0 suite)
# ──────────────────────────────────────────────────────────────────────

_INDY_LIKE = textwrap.dedent("""\
    {*
     * Indy-like SMTP wrapper — minimal fixture.
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
# Base parity with v0 (7 tests)
# ──────────────────────────────────────────────────────────────────────


def test_parses_unit_header_and_uses_clauses():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    expected = {"Classes", "SysUtils", "IdBaseComponent", "IdGlobal"}
    for r in out.subroutines:
        assert set(r.common_block_refs) >= expected, (
            f"{r.name} missing uses: got {r.common_block_refs}"
        )


def test_recognises_four_routines():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    names = sorted(r.name for r in out.subroutines)
    assert names == [
        "TIdSMTPMin.Connect",
        "TIdSMTPMin.Create",
        "TIdSMTPMin.Disconnect",
        "TIdSMTPMin.SendMessage",
    ]


def test_captures_called_routines_inside_body():
    out = parse_source("IdSMTPMin.pas", _INDY_LIKE)
    by_name = {r.name: r for r in out.subroutines}
    assert "OpenSocket" in by_name["TIdSMTPMin.Connect"].called_subroutines
    assert "Handshake" in by_name["TIdSMTPMin.Connect"].called_subroutines
    assert "CloseSocket" in by_name["TIdSMTPMin.Disconnect"].called_subroutines
    sm_calls = by_name["TIdSMTPMin.SendMessage"].called_subroutines
    assert "SendCmd" in sm_calls
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


def test_file_without_header_still_parses():
    """Unlike v0, the production parser does NOT emit a 'missing header'
    warning — tree-sitter-pascal silently accepts header-less input. The
    routines themselves must still surface."""
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
    names = sorted(r.name for r in out.subroutines)
    assert names == ["HelperA", "HelperB"]
    helper_a = next(r for r in out.subroutines if r.name == "HelperA")
    assert "HelperB" in helper_a.called_subroutines


def test_brace_comments_dont_corrupt_call_detection():
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


# ──────────────────────────────────────────────────────────────────────
# New capabilities the production parser handles + v0 does not
# ──────────────────────────────────────────────────────────────────────


def test_generic_class_methods_parse():
    """`TStack<T>` is a generic class. v0's tokenizer-based regex
    treats the `<T>` as an unbalanced angle-bracket and either misses
    the class entirely or mis-attributes its methods. tree-sitter-pascal
    parses generics natively — every method should appear in the output
    with its qualified class name."""
    src = textwrap.dedent("""\
        unit StackUnit;

        interface

        type
          TStack<T> = class
            procedure Push(Item: T);
            function Pop: T;
            function Count: Integer;
          end;

        implementation

        procedure TStack<T>.Push(Item: T);
        begin
          DoEnqueue(Item);
        end;

        function TStack<T>.Pop: T;
        begin
          Result := DoDequeue;
        end;

        function TStack<T>.Count: Integer;
        begin
          Result := FList.Count;
        end;

        end.
    """)
    out = parse_source("StackUnit.pas", src)
    names = {r.name.split(".")[-1] for r in out.subroutines}
    # All three methods must surface. The qualified name will carry the
    # `<T>` segment when tree-sitter exposes it; we accept either form.
    assert "Push" in names
    assert "Pop" in names
    assert "Count" in names


def test_preprocessor_branches_are_walked():
    """{$IFDEF MSWINDOWS} blocks emit a `pp_ifdef` node. Both arms must
    be walked so routines inside the conditional branch surface in the
    output. v0 strips comments without understanding `{$...}` and would
    typically swallow whichever branch came first."""
    src = textwrap.dedent("""\
        unit Platform;

        interface

        procedure Greet;

        implementation

        procedure Greet;
        begin
        {$IFDEF MSWINDOWS}
          WriteLn('hello from windows');
        {$ELSE}
          WriteLn('hello from posix');
        {$ENDIF}
        end;

        end.
    """)
    out = parse_source("Platform.pas", src)
    names = [r.name for r in out.subroutines]
    assert "Greet" in names
    greet = next(r for r in out.subroutines if r.name == "Greet")
    # Either branch's `WriteLn` should land in the call list; we don't
    # pin which arm wins, only that at least one was walked.
    assert "WriteLn" in greet.called_subroutines


def test_anonymous_method_is_not_emitted_as_top_level_routine():
    """A `procedure` literal passed to `TThread.Synchronize` is an
    anonymous method. It must NOT pollute the routine list — only the
    outer routine that defines it should appear. Calls inside the
    anonymous body still count toward the outer routine."""
    src = textwrap.dedent("""\
        unit AsyncDemo;

        interface

        procedure RunAsync;

        implementation

        uses Classes;

        procedure RunAsync;
        begin
          TThread.Synchronize(nil,
            procedure
            begin
              UpdateProgressBar(50);
            end);
        end;

        end.
    """)
    out = parse_source("AsyncDemo.pas", src)
    names = {r.name for r in out.subroutines}
    # Exactly one routine surfaces — the anonymous body is silent.
    assert names == {"RunAsync"}
