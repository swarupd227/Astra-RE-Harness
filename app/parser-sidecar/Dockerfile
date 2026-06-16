FROM python:3.12-slim AS base

WORKDIR /app

# System deps:
#   - ca-certificates: TLS for grpcio + the curl below
#   - gcc + curl + libc6-dev + python3-dev: needed to build tree-sitter-pascal
#     from the upstream sdist (Phase 9.4.a per ADR-031). The slim image
#     ships gcc bytecode but not the matching libc6-dev / Python headers,
#     so a bare `pip install <tarball>` would fail at C compile time.
#   - cmake: needed by the C++ production parser (Phase 9.4.b per ADR-028)
#     to bootstrap `compile_commands.json` on CMake-based corpora that
#     don't ship one. The Debian bookworm-slim apt repo ships cmake 3.25
#     which is well above the minimum for fmt/range-v3/etc.
#   - libstdc++-12-dev: provides the C++ standard library headers
#     (<string>, <vector>, <type_traits>, ...) that libclang needs to
#     resolve in real C++ corpora. Without them libclang's parse tree
#     degrades silently — typed function bodies aren't analyzed and
#     CALL_EXPR cursors don't fire.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates curl gcc g++ libc6-dev python3-dev cmake \
 && rm -rf /var/lib/apt/lists/*
# g++ pulls in libstdc++-N-dev as a dependency — that's the C++
# standard library headers libclang needs at runtime. The exact N is
# whatever the base image's g++ ships (currently g++-14 on python:3.12-
# slim = Debian trixie). Verify with `dpkg -l | grep libstdc++-*-dev`
# after the build; if the discovery paths in cpp_parser_libclang.py
# diverge after a base-image bump, retarget the -I flags there.

# Install Python deps
COPY pyproject.toml ./
RUN pip install --no-cache-dir \
        "grpcio>=1.66.0" \
        "grpcio-tools>=1.66.0" \
        "grpcio-health-checking>=1.66.0" \
        "protobuf>=5.27.0" \
        "fparser>=0.1.4" \
        # Phase 9.4.a — Delphi production parser per ADR-031. The tree-sitter
        # Python runtime is pinned to 0.21.x because Isopod/tree-sitter-pascal
        # v0.10.2's setup.py requires the older Language(ptr, name) API; the
        # 0.22+ runtime drops it. Pinned-then-tested-when-bumped pattern.
        "tree-sitter==0.21.3" \
        # Phase 9.4.b — C++ production parser per ADR-028. The PyPI libclang
        # package bundles its own native libclang.so so we don't need apt's
        # libclang-15-dev (which would add ~120 MB to the image).
        "libclang==18.1.1" \
        # Phase 10.0.a — VB6 production parser per ADR-035. Runtime for the
        # ANTLR4-generated lexer + parser modules. The 4.13.x runtime
        # matches the version used to generate the grammar stubs.
        "antlr4-python3-runtime==4.13.2"

# Phase 10.0.a.2 — vendor Rubberduck VBA's VisualBasic6.g4 grammar and
# generate Python lexer + parser stubs. Until this step runs (10.0.a.2),
# parser_sidecar/vb6_parser_antlr.py raises ImportError on first call and
# the dispatcher falls back to vb6_parser.py (v0). This stays commented
# out in v0 so the image builds without the antlr4 JAR; uncomment when
# the grammar lands.
#
# RUN apt-get update && apt-get install -y --no-install-recommends openjdk-17-jre-headless \
#  && curl -fsSL https://www.antlr.org/download/antlr-4.13.2-complete.jar -o /opt/antlr4.jar \
#  && curl -fsSL https://raw.githubusercontent.com/rubberduck-vba/Rubberduck/next/Rubberduck.Parsing/Grammar/VisualBasic6.g4 \
#       -o /tmp/VisualBasic6.g4 \
#  && java -jar /opt/antlr4.jar -Dlanguage=Python3 -o parser_sidecar/vb6_antlr_gen /tmp/VisualBasic6.g4 \
#  && apt-get purge -y openjdk-17-jre-headless && rm -rf /var/lib/apt/lists/* /opt/antlr4.jar

# Fetch + install Isopod/tree-sitter-pascal v0.10.2. It builds a small native
# extension (~100 KB after strip) that exposes a single `language()` callable.
RUN curl -fsSL https://codeload.github.com/Isopod/tree-sitter-pascal/tar.gz/refs/tags/v0.10.2 \
        -o /tmp/tspascal.tgz \
 && pip install --no-cache-dir /tmp/tspascal.tgz \
 && rm /tmp/tspascal.tgz

# curl was only needed for the tree-sitter-pascal sdist download.
# gcc / g++ / libstdc++-N-dev / libc6-dev / python3-dev all STAY:
#   - g++ + libstdc++-N-dev: C++ stdlib headers (<string>, <vector>, ...)
#     libclang needs to resolve typed bodies in real C++ corpora.
#   - gcc: its internal headers (stddef.h, stdarg.h, ...) under
#     /usr/lib/gcc/x86_64-linux-gnu/<v>/include/. Without these, libclang
#     fires "stddef.h not found" warnings on every parse and degrades
#     CALL_EXPR detection in routines that touch size_t / NULL.
#   - libc6-dev + python3-dev: stay because the auto-remove cascade
#     would otherwise strip them along with their dependents.
# This costs ~80 MB of image size vs a stripped-down build; acceptable
# for v1 robust parsing.
RUN apt-get purge -y curl \
 && rm -rf /var/lib/apt/lists/*

# Copy proto + source
COPY proto/ proto/
COPY parser_sidecar/ parser_sidecar/

# Generate Python stubs from the proto (builds parser_pb2.py + parser_pb2_grpc.py
# inside the package directory).
RUN python -m grpc_tools.protoc \
        -I=proto \
        --python_out=parser_sidecar \
        --grpc_python_out=parser_sidecar \
        proto/parser.proto

# Fix the relative import in the generated _grpc.py module so it works
# when the package is imported as parser_sidecar.parser_pb2_grpc.
RUN sed -i 's/^import parser_pb2 as parser__pb2$/from . import parser_pb2 as parser__pb2/' \
        parser_sidecar/parser_pb2_grpc.py

ENV PYTHONUNBUFFERED=1 \
    PARSER_GRPC_PORT=50051

EXPOSE 50051
CMD ["python", "-m", "parser_sidecar.server"]
