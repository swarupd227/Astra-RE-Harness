"""
Astra parser sidecar — gRPC server.

Serves on PARSER_GRPC_PORT (default 50051). Exposes:

  - Ping(empty) -> PingReply              liveness for the API /health/ready check
  - EchoSource(...) -> EchoSourceReply    round-trip test
  - Parse(...) -> ParseResult             Phase C: real fparser2-backed parse

The proto/parser.proto file is the contract. Generated Python stubs
(parser_pb2.py, parser_pb2_grpc.py) are produced at container build time.
"""

from __future__ import annotations

import logging
import os
import signal
import sys
import threading
import time
from concurrent import futures

import grpc
from grpc_health.v1 import health, health_pb2, health_pb2_grpc

from parser_sidecar import parser_pb2, parser_pb2_grpc, __version__
from parser_sidecar.fortran_parser import parse_source as _fortran_parse
from parser_sidecar.cobol_parser import parse_source as _cobol_parse
from parser_sidecar.delphi_parser import parse_source as _delphi_parse_v0
from parser_sidecar.cpp_parser import parse_source as _cpp_parse_v0
from parser_sidecar.vb6_parser import parse_source as _vb6_parse_v0

# Phase 9.4.a — production Delphi parser (tree-sitter-pascal per ADR-031).
# Imported lazily so a missing/broken grammar binding doesn't take the
# sidecar down at startup; v0 stays as the fallback. The module loads on
# first parse request inside `_delphi_parse`.
_delphi_parse_ts: "any | None" = None
# Phase 9.4.b — production C++ parser (libclang per ADR-028). Same
# lazy-import + v0-fallback shape.
_cpp_parse_libclang: "any | None" = None
# Phase 10.0.a — production VB6 parser (ANTLR4 per ADR-035). Same
# lazy-import + v0-fallback shape; the ANTLR4 grammar lands in 10.0.a.2.
_vb6_parse_antlr: "any | None" = None


def _delphi_parse(filename: str, content: str):
    """Dispatch: prefer the tree-sitter-pascal parser; fall through to the
    v0 tokenizer on import error or grammar panic. The dispatcher records
    the fallback in the returned `warnings` list so the Migration Planner
    can surface it."""
    global _delphi_parse_ts
    if _delphi_parse_ts is None:
        try:
            from parser_sidecar.delphi_parser_tree_sitter import parse_source as _ts
            _delphi_parse_ts = _ts
        except Exception as e:  # noqa: BLE001 — tree-sitter import surface is broad
            log.warning("tree-sitter-pascal unavailable (%s); using v0 tokenizer", e)
            _delphi_parse_ts = False
    if _delphi_parse_ts:
        try:
            return _delphi_parse_ts(filename=filename, content=content)
        except Exception as e:  # noqa: BLE001 — grammar panics surface broadly
            log.info("tree-sitter-pascal panic on %s (%s); v0 fallback", filename, e)
            outcome = _delphi_parse_v0(filename=filename, content=content)
            outcome.warnings.append(f"tree-sitter-pascal panic: {e}; v0 fallback")
            return outcome
    return _delphi_parse_v0(filename=filename, content=content)


def _vb6_parse(filename: str, content: str):
    """Dispatch: prefer the ANTLR4 production parser; fall through to the
    v0 tokenizer on grammar absence or panic. Same fallback-on-warnings
    shape as the Delphi and C++ dispatchers."""
    global _vb6_parse_antlr
    if _vb6_parse_antlr is None:
        try:
            from parser_sidecar.vb6_parser_antlr import parse_source as _a
            _vb6_parse_antlr = _a
        except Exception as e:  # noqa: BLE001 — ANTLR4 import surface is broad
            log.info("vb6 ANTLR4 grammar unavailable (%s); using v0 tokenizer", e)
            _vb6_parse_antlr = False
    if _vb6_parse_antlr:
        try:
            return _vb6_parse_antlr(filename=filename, content=content)
        except Exception as e:  # noqa: BLE001 — grammar panics surface broadly
            log.info("vb6 ANTLR4 panic on %s (%s); v0 fallback", filename, e)
            outcome = _vb6_parse_v0(filename=filename, content=content)
            outcome.warnings.append(f"vb6 ANTLR4 panic: {e}; v0 fallback")
            return outcome
    return _vb6_parse_v0(filename=filename, content=content)


def _cpp_parse(filename: str, content: str):
    """Dispatch: prefer the libclang production parser; fall through to
    the v0 tokenizer on libclang import error or LibclangError. Same
    fallback-on-warnings shape as the Delphi dispatcher."""
    global _cpp_parse_libclang
    if _cpp_parse_libclang is None:
        try:
            from parser_sidecar.cpp_parser_libclang import parse_source as _lc
            _cpp_parse_libclang = _lc
        except Exception as e:  # noqa: BLE001 — libclang import surface is broad
            log.warning("libclang unavailable (%s); using v0 C++ tokenizer", e)
            _cpp_parse_libclang = False
    if _cpp_parse_libclang:
        try:
            return _cpp_parse_libclang(filename=filename, content=content)
        except Exception as e:  # noqa: BLE001 — libclang panics surface broadly
            log.info("libclang panic on %s (%s); v0 fallback", filename, e)
            outcome = _cpp_parse_v0(filename=filename, content=content)
            outcome.warnings.append(f"libclang panic: {e}; v0 fallback")
            return outcome
    return _cpp_parse_v0(filename=filename, content=content)

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-parser","msg":"%(message)s"}',
)
log = logging.getLogger("astra.parser")

# fparser2 keeps a global SymbolTable singleton (fparser.two.symbol_table.
# SYMBOL_TABLES) that is mutated as the parser walks scopes. Concurrent
# parses corrupt it — the failure mode is `SymbolTableError: exit_scope()
# called but no current scope exists`. Serialising every Parse RPC behind
# a process-wide lock is the simplest correct fix; throughput cost is
# negligible because a single file parses in ≤1 s and the API only ever
# issues one corpus-ingest at a time per worker.
_PARSE_LOCK = threading.Lock()


class ParserServicer(parser_pb2_grpc.ParserServicer):
    def Ping(self, request, context):
        return parser_pb2.PingReply(
            service="astra-parser",
            version=__version__,
            epoch_ms=int(time.time() * 1000),
        )

    def EchoSource(self, request, context):
        content = request.content or ""
        return parser_pb2.EchoSourceReply(
            filename=request.filename,
            line_count=content.count("\n") + (1 if content and not content.endswith("\n") else 0),
            byte_length=len(content.encode("utf-8")),
        )

    def Parse(self, request, context):
        filename = request.filename or "<inline>.f90"
        # Phase 5.1 + 9.0.a: route by extension. COBOL, Delphi, and
        # Fortran each have their own parser; the contract is identical
        # so the API sees one unified `ParseResult` shape regardless of
        # source language.
        is_cobol = _looks_like_cobol(filename)
        is_delphi = (not is_cobol) and _looks_like_delphi(filename)
        is_cpp = (not is_cobol) and (not is_delphi) and _looks_like_cpp(filename)
        is_vb6 = (not is_cobol) and (not is_delphi) and (not is_cpp) and _looks_like_vb6(filename)
        # fparser2 is the only parser that needs the process-wide lock
        # (global SymbolTable singleton); the COBOL / Delphi / C++ / VB6 paths
        # are purely local. We acquire the lock only for the Fortran path.
        if is_cobol:
            cobol_outcome = _cobol_parse(filename=filename, content=request.content or "")
            outcome_line_count = cobol_outcome.line_count
            outcome_subroutines = cobol_outcome.subroutines
            outcome_warnings = cobol_outcome.warnings
            outcome_filename = filename
            language = "cobol"
        elif is_delphi:
            delphi_outcome = _delphi_parse(filename=filename, content=request.content or "")
            outcome_line_count = delphi_outcome.line_count
            outcome_subroutines = delphi_outcome.subroutines
            outcome_warnings = delphi_outcome.warnings
            outcome_filename = delphi_outcome.filename
            language = "delphi"
        elif is_cpp:
            cpp_outcome = _cpp_parse(filename=filename, content=request.content or "")
            outcome_line_count = cpp_outcome.line_count
            outcome_subroutines = cpp_outcome.subroutines
            outcome_warnings = cpp_outcome.warnings
            outcome_filename = cpp_outcome.filename
            language = "cpp"
        elif is_vb6:
            vb6_outcome = _vb6_parse(filename=filename, content=request.content or "")
            outcome_line_count = vb6_outcome.line_count
            outcome_subroutines = vb6_outcome.subroutines
            outcome_warnings = vb6_outcome.warnings
            outcome_filename = vb6_outcome.filename
            language = "vb6"
        else:
            with _PARSE_LOCK:
                fortran_outcome = _fortran_parse(
                    content=request.content or "",
                    filename=filename,
                    form=request.form or "",
                )
            outcome_line_count = fortran_outcome.line_count
            outcome_subroutines = fortran_outcome.subroutines
            outcome_warnings = fortran_outcome.warnings
            outcome_filename = fortran_outcome.filename
            language = "fortran"

        result = parser_pb2.ParseResult(
            filename=outcome_filename,
            line_count=outcome_line_count,
            warnings=list(outcome_warnings),
        )
        for s in outcome_subroutines:
            result.subroutines.add(
                name=s.name,
                signature=s.signature,
                line_start=s.line_start,
                line_end=s.line_end,
                common_block_refs=list(s.common_block_refs),
                called_subroutines=list(s.called_subroutines),
            )
        log.info(
            "parsed lang=%s file=%s lines=%d subroutines=%d warnings=%d",
            language,
            filename,
            outcome_line_count,
            len(outcome_subroutines),
            len(outcome_warnings),
        )
        return result


def _looks_like_cobol(filename: str) -> bool:
    """Return True when the file path's extension marks it as COBOL.

    Phase 5: .cob / .cbl / .cpy (and uppercase variants). Detection by
    extension is deliberately strict; magic-byte sniffing arrives in
    Phase 7 when copybook headers + DBCS encodings need handling.
    """
    if not filename:
        return False
    lower = filename.lower()
    return lower.endswith((".cob", ".cbl", ".cpy"))


def _looks_like_delphi(filename: str) -> bool:
    """Return True when the file path's extension marks it as Delphi /
    Object Pascal source.

    Phase 9.0.a: .pas (units), .dpr (program), .dpk (package),
    .inc (include file). Strict by-extension routing; the v0 parser
    handles all four uniformly.
    """
    if not filename:
        return False
    lower = filename.lower()
    return lower.endswith((".pas", ".dpr", ".dpk", ".inc"))


def _looks_like_vb6(filename: str) -> bool:
    """Return True when the file path's extension marks it as VB6 source.

    Phase 10.0.a: .bas (modules), .cls (class modules), .frm (forms +
    code-behind). `.vbp` (project file) and `.vbg` (group file) are
    metadata, not source — they're consumed by the IngestPipeline, not
    the parser. `.ctl` (user controls) is treated like `.frm` (same
    property-bag + code shape).
    """
    if not filename:
        return False
    lower = filename.lower()
    return lower.endswith((".bas", ".cls", ".frm", ".ctl"))


def _looks_like_cpp(filename: str) -> bool:
    """Return True when the file path's extension marks it as C++ source.

    Phase 9.1.a: .cpp / .cc / .cxx / .c++ for translation units, and
    .h / .hpp / .hxx / .h++ / .ipp for headers (which the v0 parser
    treats uniformly — header-only libraries like fmt have their
    "definitions" in the .h files). Strict by-extension routing; the
    production libclang path consumes a `compile_commands.json`
    populated per ADR-028.
    """
    if not filename:
        return False
    lower = filename.lower()
    return lower.endswith((
        ".cpp", ".cc", ".cxx", ".c++",
        ".hpp", ".hxx", ".h++", ".ipp",
        # `.h` is ambiguous (could be C or C++); v0 routes it to C++
        # because all our seeded corpora are C++. If a pure-C corpus
        # arrives later we'll add a `.c` → C-mode branch.
        ".h",
    ))


def serve() -> None:
    port = int(os.environ.get("PARSER_GRPC_PORT", "50051"))
    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=8),
        # Large enough for typical Fortran files; bump if a real corpus needs more.
        options=[
            ("grpc.max_receive_message_length", 32 * 1024 * 1024),
            ("grpc.max_send_message_length", 32 * 1024 * 1024),
        ],
    )

    parser_pb2_grpc.add_ParserServicer_to_server(ParserServicer(), server)

    health_servicer = health.HealthServicer()
    health_servicer.set("astra.parser.v1.Parser", health_pb2.HealthCheckResponse.SERVING)
    health_servicer.set("", health_pb2.HealthCheckResponse.SERVING)
    health_pb2_grpc.add_HealthServicer_to_server(health_servicer, server)

    server.add_insecure_port(f"[::]:{port}")
    server.start()
    log.info("Parser sidecar listening on :%s (version %s)", port, __version__)

    def _shutdown(signum, _frame):
        log.info("Received signal %s, shutting down", signum)
        server.stop(grace=5).wait()
        sys.exit(0)

    signal.signal(signal.SIGTERM, _shutdown)
    signal.signal(signal.SIGINT, _shutdown)
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
