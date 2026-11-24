Legacy .xls fixtures for the BIFF8 reader (Office/LegacyWorkbook), all from MIT-licensed sources:

- `formula_references.xls` — tafia/calamine, tests/formula_references.xls (MIT, see LICENSE-calamine.txt). Written by
  Excel; relative, absolute and mixed references on one sheet and to another sheet. calamine's own test
  (tests/test.rs, test_xls_formula_references) states the expected formula text, which our test repeats.
- `Check_this_out.xls` — Pepitoh/VBad, Example/Results/Check_this_out.xls (MIT, see LICENSE-vbad.txt). Written by Excel; a
  VBA project with a Workbook_Open event, a very hidden sheet and a visible one.
- `Installer-from-xlsm.xls` — the VBA-Web installer workbook (`../vba-web/VBA-Web - Installer.xlsm`, MIT, see
  `../vba-web/LICENSE`) converted to .xls by LibreOffice 7.4 (`soffice --convert-to xls:"MS Excel 97"` with its
  "save original Basic code" option on). It is the one fixture with form controls and defined names: 17 named
  cells, two buttons that run macros, 13 check boxes linked to cells, and the VBA project. LibreOffice, not Excel,
  wrote it, so it pins what the reader makes of this file, not a statement about Excel's output.
