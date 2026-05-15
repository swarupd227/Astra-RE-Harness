"""
Unit tests for the COBOL-85 parser module.

Uses inlined COBOL source (verbatim from the openmainframeproject
cobol-programming-course repo with whitespace preserved) so the test
suite stays self-contained.
"""
from __future__ import annotations

import unittest

from parser_sidecar.cobol_parser import parse_source


DEPTPAY = """\
       IDENTIFICATION DIVISION.
       PROGRAM-ID. DEPTPAY.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  DEPT-RECORD.
           05  DEPT-NAME            PIC X(20).
           05  DEPT-LOC             PIC X(12).
       PROCEDURE DIVISION.
           PERFORM AVERAGE-SALARY.
           PERFORM DISPLAY-DETAILS.
           STOP RUN.

       AVERAGE-SALARY.
           MOVE "FINANCE"           TO DEPT-NAME.
           MOVE "SOUTHWEST"         TO DEPT-LOC.
      *****
       DISPLAY-DETAILS.
           DISPLAY "Department Name: " DEPT-NAME.
           DISPLAY "Department Location: " DEPT-LOC.
"""

EMPPAY_WITH_NESTED_IF = """\
       IDENTIFICATION DIVISION.
       PROGRAM-ID. EMPPAY.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  EMP-HOURS                PIC 9(3).
       PROCEDURE DIVISION.
           PERFORM INITIALIZATION.
           PERFORM PAYMENT-WEEKLY.
           STOP RUN.

       INITIALIZATION.
           MOVE 19                  TO EMP-HOURS.

       PAYMENT-WEEKLY.
           IF  EMP-HOURS >= 40
               PERFORM SHOW-OUTPUT UNTIL EMP-HOURS = 0
           ELSE
               DISPLAY "OK".

       SHOW-OUTPUT.
           DISPLAY "Done".
"""

WITH_COPYBOOK = """\
       IDENTIFICATION DIVISION.
       PROGRAM-ID. WITHCOPY.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
           COPY ACCTREC.
           COPY PRTLINE.
       PROCEDURE DIVISION.
           PERFORM MAIN.

       MAIN.
           DISPLAY "OK".
"""

NO_PROGRAM_ID = """\
       IDENTIFICATION DIVISION.
       AUTHOR. Otto B. Boolean.
       DATA DIVISION.
       PROCEDURE DIVISION.
           DISPLAY "NO PID".
"""


class CobolParserTests(unittest.TestCase):
    def test_deptpay_identifies_program_and_paragraphs(self):
        outcome = parse_source(filename="DEPTPAY.CBL", content=DEPTPAY)
        self.assertEqual(outcome.warnings, [])
        self.assertEqual(len(outcome.subroutines), 1)
        sub = outcome.subroutines[0]
        self.assertEqual(sub.name, "DEPTPAY")
        self.assertEqual(sub.signature, "PROGRAM-ID. DEPTPAY.")
        self.assertEqual(sub.line_start, 1)
        # 19 non-empty lines in the inline source above (no trailing blank
        # is significant); the end is the last non-empty line.
        self.assertGreaterEqual(sub.line_end, 18)
        self.assertEqual(list(sub.called_subroutines), ["AVERAGE-SALARY", "DISPLAY-DETAILS"])
        self.assertEqual(list(sub.common_block_refs), [])

    def test_nested_if_does_not_misread_perform_until_as_paragraph(self):
        outcome = parse_source(filename="EMPPAY.CBL", content=EMPPAY_WITH_NESTED_IF)
        self.assertEqual(outcome.warnings, [])
        sub = outcome.subroutines[0]
        # PERFORM SHOW-OUTPUT UNTIL ... should yield SHOW-OUTPUT, not UNTIL.
        self.assertIn("SHOW-OUTPUT", sub.called_subroutines)
        self.assertNotIn("UNTIL", sub.called_subroutines)
        # PERFORM SHOW-OUTPUT should be deduped against an explicit
        # paragraph at SHOW-OUTPUT.
        self.assertEqual(sub.called_subroutines.count("SHOW-OUTPUT"), 1)
        # Always-called paragraphs from the top of PROCEDURE DIVISION
        self.assertIn("INITIALIZATION", sub.called_subroutines)
        self.assertIn("PAYMENT-WEEKLY", sub.called_subroutines)

    def test_copy_books_surface_via_common_block_refs(self):
        outcome = parse_source(filename="WITHCOPY.cbl", content=WITH_COPYBOOK)
        self.assertEqual(outcome.warnings, [])
        sub = outcome.subroutines[0]
        self.assertEqual(sub.name, "WITHCOPY")
        self.assertEqual(set(sub.common_block_refs), {"ACCTREC", "PRTLINE"})
        self.assertEqual(list(sub.called_subroutines), ["MAIN"])

    def test_missing_program_id_emits_warning_and_no_subroutines(self):
        outcome = parse_source(filename="bad.cbl", content=NO_PROGRAM_ID)
        self.assertEqual(outcome.subroutines, [])
        self.assertTrue(any("PROGRAM-ID" in w for w in outcome.warnings))

    def test_comment_lines_dont_register_as_paragraphs(self):
        src = """\
       IDENTIFICATION DIVISION.
       PROGRAM-ID. CMTTEST.
       PROCEDURE DIVISION.
      * THIS IS A COMMENT WITH A PERIOD.
       MAIN.
           PERFORM SUB-A.
       SUB-A.
           DISPLAY "X".
"""
        outcome = parse_source(filename="CMTTEST.CBL", content=src)
        sub = outcome.subroutines[0]
        self.assertEqual(sub.name, "CMTTEST")
        self.assertEqual(list(sub.called_subroutines), ["SUB-A"])

    def test_indicator_column_slash_treated_as_comment(self):
        # Some COBOL dialects use '/' in col 7 to force a page break;
        # it must also be treated as a comment line by the parser.
        src = "      /THIS IS A COMMENT/PAGE BREAK\n" + DEPTPAY
        outcome = parse_source(filename="WITHPB.cbl", content=src)
        self.assertEqual(outcome.warnings, [])
        self.assertEqual(outcome.subroutines[0].name, "DEPTPAY")


if __name__ == "__main__":
    unittest.main()
