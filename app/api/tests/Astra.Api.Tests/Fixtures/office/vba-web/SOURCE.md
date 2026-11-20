Copied from https://github.com/VBA-tools/VBA-Web (MIT, see LICENSE) as a test fixture for the Office/VBA reader. Both the workbook (VBA-Web - Blank.xlsm) and the exported modules in src/ are taken from the same commit, 25d0a0c9b13c62bf091ed4bd15e3663bf371c7a2 (release v4.1.6), the last commit that changed the workbook, so the exports are an independent ground truth for what the workbook contains.

VBA-Web - Installer.xlsm (same repository and licence) comes from the master branch
(sha256 E380B3B11F28F932CCF439177CA0DA948560D9F6796E98EE602A65F3A4F5DAAB). It is the fixture for the
workbook-surface reader: one sheet (code name Install) with two form buttons running Install.Install and
Install.Browse and thirteen check boxes, each linked to a cell in column C.
