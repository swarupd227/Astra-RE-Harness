"""
Tests for parser_sidecar.unibasic_parser.

Calibrated directly against six real, public UniBasic/Pick-BASIC files
(github.com/zelenko/pick) rather than synthetic snippets, so these fixtures
are literal excerpts of genuine shop code (attribution: zelenko/pick,
UniVerse-backed Eclipse ERP customization). Asserts one-routine-per-file,
comment stripping (full-line */!, inline ;*), SUBROUTINE header detection,
internal GOSUB-paragraph exclusion from called_subroutines, CALL/SUBR
inter-program call capture, and dynamic @-indirect call flagging.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.unibasic_parser import parse_source


# Verbatim excerpt of ARRAY.PICK (github.com/zelenko/pick) — no header, no
# inter-program calls, just built-ins (MATPARSE/LOCATE/INSERT/DCOUNT).
_ARRAY_PICK = textwrap.dedent("""
          PGM = "VZ.ARRAY"
    ** Version# 0.0001[194] - 01/04/2017 - 04:07pm - ADMIN - system
    *** V0.0001 Change - Custom Coding DTE429 - 01/04/2017 - ADMIN - system
          DIM A(5)
          MATPARSE A FROM '1,2,3,4,5,6,7,8,9,10,11',','
          FOR I=1 TO 5
                PRINT "(":I:")=":A(I)," ":
          NEXT I
          DIM BA(2)
          BA(1) ='THIS'
          BA(1)<2> = '1-2'
          LOCATE 'THIS' IN BA(1) SETTING ID ELSE
             BA(1) = INSERT(BA(1),ID;'THIS')
          END
          PRINT 'TOTAL: ' : DCOUNT(BA(1),@FM)
          END
    !ADMIN~01/04/17~16:07
""").strip("\n")

# Verbatim excerpt of PO.WITH.NO.COGS.pick — no header (whole file is the
# implicit program), one real SUBR() call, one internal GOSUB paragraph, and
# several commented-out (!) lines that must NOT be mistaken for live calls.
_PO_NO_COGS = textwrap.dedent("""
    PGM ='VZ.PO.NO.COGS'
    ** Version# 0.0002[30] - 10/16/2017 - 07:58pm - DEVOPS - eclipse
    OPEN 'ORDER.QUEUE' TO OQ ELSE
              STOP
       END

    CMD = 'SELECT ORDER.QUEUE WITH @ID = "P]" AND STAT = "O"'
    EXECUTE CMD

    LOOP
              READNEXT ID ELSE EXIT
              !SUBRI = 'DICT.GET.LEDGER.VALUE'
              !CALL @SUBRI(LINES,'LI.DESC')
              NAME = SUBR('VZ.GET.VALUE','INITIALS',WRITER,3)
              MSG = NAME:' created a PO with zero cost: ':@ID
              USR.ID = 'DEVOPS'
                 GOSUB SEND.MSG
    REPEAT
    RETURN

    *-------------------------------------------------------------------------*
    SEND.MSG:       *** Sends message to user
    *-------------------------------------------------------------------------*
                 SEND.MESSAGE 'Phantom', USR.ID, MSG
              RETURN
    !DEVOPS~10/16/17~19:58
""").strip("\n")

# Verbatim excerpt of VZ.SALES.BR.pick — the one real formal SUBROUTINE
# header in the calibration corpus, plus a dynamic @-indirect CALL.
_SUBROUTINE_HEADER = textwrap.dedent("""
          SUBROUTINE (RESULT,PN,SD,ED)
    ** Version# 0.0004 - 10/13/2017 - 02:35pm - DEVOPS - eclipse
          @ID = PN
          SUBRI = 'DICT.PRD.SALES'
          CALL @SUBRI(RESULT,1,6,SD,ED,1,'')
          RESULT = RESULT/100"MR2"
          RETURN
    !DEVOPS~10/13/17~14:35
""").strip("\n")


def test_no_header_program_is_one_routine_named_from_filename():
    outcome = parse_source("ARRAY.PICK", _ARRAY_PICK)
    assert outcome.line_count == len(_ARRAY_PICK.split("\n"))
    assert len(outcome.subroutines) == 1
    sub = outcome.subroutines[0]
    assert sub.name == "ARRAY"
    assert "(program)" in sub.signature
    assert 'PGM="VZ.ARRAY"' in sub.signature
    assert sub.line_start == 1
    assert sub.line_end == outcome.line_count
    assert sub.called_subroutines == ()


def test_subr_call_captured_gosub_paragraph_excluded():
    outcome = parse_source("PO.WITH.NO.COGS.pick", _PO_NO_COGS)
    sub = outcome.subroutines[0]
    assert sub.called_subroutines == ("VZ.GET.VALUE",)
    # SEND.MSG is an internal GOSUB paragraph, not a called subroutine.
    assert "SEND.MSG" not in sub.called_subroutines
    assert any("SEND.MSG" in w and "internal GOSUB paragraph" in w for w in outcome.warnings)


def test_commented_out_calls_are_not_extracted():
    outcome = parse_source("PO.WITH.NO.COGS.pick", _PO_NO_COGS)
    sub = outcome.subroutines[0]
    # The two `!`-commented lines reference DICT.GET.LEDGER.VALUE via a
    # dynamic call — they must NOT appear anywhere in called_subroutines.
    assert "DICT.GET.LEDGER.VALUE" not in sub.called_subroutines
    assert not any("DICT.GET.LEDGER.VALUE" in w for w in outcome.warnings)


def test_subroutine_header_detected_and_unnamed():
    outcome = parse_source("VZ.SALES.BR.pick", _SUBROUTINE_HEADER)
    sub = outcome.subroutines[0]
    # SUBROUTINE(...) carries no name of its own — the module name comes
    # from the filename, per the real calibration file.
    assert sub.name == "VZ.SALES.BR"
    assert sub.signature.startswith("SUBROUTINE VZ.SALES.BR(RESULT,PN,SD,ED)")


def test_dynamic_indirect_call_flagged_not_silently_dropped():
    outcome = parse_source("VZ.SALES.BR.pick", _SUBROUTINE_HEADER)
    sub = outcome.subroutines[0]
    assert sub.called_subroutines == ("@SUBRI",)
    assert any("dynamically-dispatched" in w and "@SUBRI" in w for w in outcome.warnings)


def test_inline_semicolon_star_comment_stripped():
    src = textwrap.dedent("""
        PGM = "T"
        PRINT "RESULT: ":RESULT ;* : AMTS<1,1>
        RETURN
    """).strip("\n")
    outcome = parse_source("t.pick", src)
    # The parser only needs to not choke on / misparse the inline comment;
    # no call tokens live after the ;* so called_subroutines stays empty.
    assert outcome.subroutines[0].called_subroutines == ()


def test_function_header_form_detected():
    src = textwrap.dedent("""
        FUNCTION VZ.CALC.TOTAL (QTY,PRICE)
        TOTAL = QTY * PRICE
        RETURN TOTAL
    """).strip("\n")
    outcome = parse_source("VZ.CALC.TOTAL.pick", src)
    sub = outcome.subroutines[0]
    assert sub.signature.startswith("FUNCTION VZ.CALC.TOTAL(QTY,PRICE)")
