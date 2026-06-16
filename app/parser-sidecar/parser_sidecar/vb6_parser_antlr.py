"""
Astra parser sidecar — VB6 production parser (ANTLR4).

**Phase 10.0.a (production skeleton).** Per ADR-035 the production VB6
parser loads an ANTLR4 grammar derived from Rubberduck VBA's
`VisualBasic6.g4`, parses the file via the antlr4-python3-runtime, and
walks the resulting parse tree to produce the canonical
`ParseOutcome` shape (same shape as `vb6_parser.parse_source`).

This module is **deliberately not yet wired up** in v0 — the grammar
files need to be vendored at container build time and the generated
lexer/parser modules need to be compiled in. The dispatcher in
`server.py` lazily imports this module and falls through to the v0
parser if the import fails (same pattern as `delphi_parser_tree_sitter`).

Bootstrap steps (10.0.a.2 follow-on):

1. Vendor Rubberduck's `Rubberduck.Parsing/Grammar/VisualBasic6.g4` into
   `proto/vb6/VisualBasic6.g4` at container build time.
2. Run `antlr4 -Dlanguage=Python3 -o parser_sidecar/vb6_antlr_gen` against
   the vendored grammar — Python lexer + parser stubs go into the
   `vb6_antlr_gen/` package.
3. Implement `_walk_parse_tree(tree)` below to produce `ParseOutcome`.
4. Verify against the seed corpus skeleton (12 forms).

Until those land, this module raises `ImportError("vb6_parser_antlr: "
"grammar not yet vendored")` at import time, which the dispatcher
catches and falls back to the v0 tokenizer.
"""
from __future__ import annotations

import logging
from typing import TYPE_CHECKING

log = logging.getLogger("astra.parser.vb6.antlr")

# Guard the import behind a try so the v0 dispatcher path never blocks
# on grammar absence. The actual import lands in 10.0.a.2.
try:
    from parser_sidecar.vb6_antlr_gen.VisualBasic6Lexer import VisualBasic6Lexer  # type: ignore
    from parser_sidecar.vb6_antlr_gen.VisualBasic6Parser import VisualBasic6Parser  # type: ignore
    _ANTLR_AVAILABLE = True
except Exception as e:  # noqa: BLE001 — grammar absence at v0 is expected
    log.info("vb6 ANTLR4 grammar not yet vendored (%s); v0 fallback active", e)
    _ANTLR_AVAILABLE = False


if TYPE_CHECKING:
    from parser_sidecar.vb6_parser import ParseOutcome


def parse_source(filename: str, content: str) -> "ParseOutcome":
    """Production VB6 parser entry point. Mirrors `vb6_parser.parse_source`'s
    contract: same signature, same `ParseOutcome` return shape.

    Raises `ImportError` until the ANTLR4 grammar is vendored — the
    dispatcher in `server.py` catches it and falls back to v0.
    """
    if not _ANTLR_AVAILABLE:
        raise ImportError(
            "vb6_parser_antlr: ANTLR4 grammar not yet vendored. "
            "Bootstrap via 10.0.a.2 — vendor VisualBasic6.g4 from "
            "github.com/rubberduck-vba/Rubberduck and run "
            "`antlr4 -Dlanguage=Python3 -o vb6_antlr_gen VisualBasic6.g4`."
        )
    raise NotImplementedError(
        "vb6_parser_antlr.parse_source: ANTLR4 walker not yet implemented "
        "(skeleton landed in 10.0.a; walker lands in 10.0.a.2)."
    )
