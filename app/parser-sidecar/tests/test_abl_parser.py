"""
Tests for parser_sidecar.abl_parser.

Phase 13.0 — smoke coverage for the OpenEdge ABL v0 tokenizer. Asserts the
parser recognises PROCEDURE / FUNCTION / METHOD blocks, the file-level main
block, nestable comments, block-depth END matching, and RUN / {include}
reference sites.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.abl_parser import parse_source


_PROGRAM = textwrap.dedent("""
    /* customer-orders.p - order entry */
    {shared-vars.i}

    DEFINE INPUT PARAMETER pcCustomer AS CHARACTER NO-UNDO.

    FIND FIRST Customer WHERE Customer.Name = pcCustomer NO-ERROR.
    RUN process-orders.

    PROCEDURE process-orders:
        FOR EACH Order WHERE Order.CustNum = Customer.CustNum:
            IF Order.Status = "OPEN" THEN DO:
                RUN post-order.p (Order.OrderNum).
            END.
        END.
    END PROCEDURE.

    FUNCTION get-order-total RETURNS INTEGER (INPUT piCust AS INTEGER):
        DEFINE VARIABLE iSum AS INTEGER NO-UNDO.
        FOR EACH Order WHERE Order.CustNum = piCust:
            iSum = iSum + Order.Amount.
        END.
        RETURN iSum.
    END FUNCTION.
""").strip()


def _by_name(out):
    return {s.name: s for s in out.subroutines}


def test_extracts_named_routines_and_main_block():
    out = parse_source("customer-orders.p", _PROGRAM)
    names = set(_by_name(out))
    assert "process-orders" in names
    assert "get-order-total" in names
    # Top-level FIND/RUN before the first PROCEDURE → a main-block routine.
    assert any(n.endswith("(main block)") for n in names)


def test_block_depth_matches_correct_end():
    out = parse_source("customer-orders.p", _PROGRAM)
    proc = _by_name(out)["process-orders"]
    # The nested FOR EACH / IF DO blocks must not end the procedure early —
    # it should span past its inner END. statements to END PROCEDURE.
    assert proc.line_end - proc.line_start >= 5


def test_run_and_include_refs_collected():
    out = parse_source("customer-orders.p", _PROGRAM)
    by = _by_name(out)
    main = next(v for k, v in by.items() if k.endswith("(main block)"))
    assert "process-orders" in main.called_subroutines
    assert "shared-vars.i" in main.common_block_refs
    assert "post-order.p" in by["process-orders"].called_subroutines


def test_forward_declaration_is_skipped():
    src = textwrap.dedent("""
        FUNCTION calc RETURNS DECIMAL (INPUT x AS DECIMAL) FORWARD.

        FUNCTION calc RETURNS DECIMAL (INPUT x AS DECIMAL):
            RETURN x * 2.
        END FUNCTION.
    """).strip()
    out = parse_source("calc.p", src)
    calcs = [s for s in out.subroutines if s.name == "calc"]
    # Only the definition counts — the FORWARD prototype is skipped.
    assert len(calcs) == 1
    assert calcs[0].line_start > 1


def test_nested_block_comment_is_stripped():
    src = textwrap.dedent("""
        /* outer /* nested */ still comment */
        PROCEDURE foo:
            MESSAGE "hi".
        END PROCEDURE.
    """).strip()
    out = parse_source("foo.p", src)
    assert any(s.name == "foo" for s in out.subroutines)


def test_no_named_routines_yields_whole_file_unit():
    src = textwrap.dedent("""
        DEFINE VARIABLE i AS INTEGER NO-UNDO.
        DISPLAY "report".
        MESSAGE "done".
    """).strip()
    out = parse_source("report.p", src)
    assert len(out.subroutines) == 1
    assert out.subroutines[0].name.startswith("report")
    assert out.subroutines[0].line_start == 1


def test_method_inside_class_tracks_container():
    src = textwrap.dedent("""
        CLASS Acme.Orders.Calculator:
            METHOD PUBLIC INTEGER AddLine(INPUT qty AS INTEGER, INPUT price AS INTEGER):
                RETURN qty * price.
            END METHOD.
        END CLASS.
    """).strip()
    out = parse_source("Calculator.p", src)
    add = next(s for s in out.subroutines if s.name == "AddLine")
    assert "Acme.Orders.Calculator" in add.common_block_refs
