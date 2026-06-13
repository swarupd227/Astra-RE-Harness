"""Phase 9.1.a — unit tests for the v0 C++ parser.

The fmt corpus is the calibration target. These fixtures model the
shapes the v0 tokenizer must handle: free functions, class methods,
constructors / destructors, namespaces, template prefixes, include
edges, and call-site detection. The production libclang+CMake path
(per ADR-028) will subsume this v0 module behind the same
ParseOutcome contract.
"""
from __future__ import annotations

from parser_sidecar.cpp_parser import parse_source


# A small fmt-flavoured fixture exercising every v0 capability.
_FMT_LIKE = """\
// Trimmed fmt-style sample for v0 parser calibration.
#include <string>
#include <vector>
#include <fmt/core.h>

namespace fmt {
namespace detail {

template<typename T>
T add(T a, T b) {
    return a + b;
}

struct format_context {
    int width;
    bool zero_pad;
    /* parses one arg from the supplied string view */
    int parse_arg(const char* p, int len) {
        if (len <= 0) return 0;
        return parse_int(p, len);
    }
};

class formatter {
public:
    formatter();
    ~formatter();
    int format(const std::string& fmtstr, int arg) {
        format_context ctx{0, false};
        return ctx.parse_arg(fmtstr.c_str(), fmtstr.size());
    }
};

formatter::formatter() : /* member init */ {}
formatter::~formatter() {}

}  // namespace detail
}  // namespace fmt
"""


def test_includes_recorded_as_common_block_refs():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    refs = set(s.common_block_refs for s in out.subroutines)
    assert len(refs) == 1
    only = next(iter(refs))
    assert "string" in only
    assert "vector" in only
    assert "fmt/core.h" in only


def test_finds_free_function_with_template():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    add = [s for s in out.subroutines if s.name == "add"]
    assert len(add) == 1
    assert "template<typename T>" in add[0].signature
    assert add[0].line_start < add[0].line_end


def test_finds_class_methods_and_constructor():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    names = {s.name for s in out.subroutines}
    # parse_arg lives inside `struct format_context` so its name is the
    # bare member name; the v0 parser doesn't synthesise the qualifier
    # when the body sits inside the struct definition. That's fine — the
    # qualified-method case below (`formatter::formatter`) exercises the
    # qualifier path.
    assert "parse_arg" in names
    assert "format" in names
    # Out-of-line constructor + destructor land as qualified names.
    assert "formatter::formatter" in names
    assert "formatter::~formatter" in names


def test_call_detection_inside_bodies():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    by_name = {s.name: s for s in out.subroutines}
    # parse_arg calls parse_int from its body.
    assert "parse_int" in by_name["parse_arg"].called_subroutines
    # formatter::format calls ctx.parse_arg — the v0 heuristic strips the
    # member-access prefix, so `parse_arg` shows up as a call.
    fmt_calls = set(by_name["format"].called_subroutines)
    assert "parse_arg" in fmt_calls


def test_line_ranges_monotonic_and_within_file():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    total_lines = out.line_count
    for s in out.subroutines:
        assert 1 <= s.line_start <= s.line_end <= total_lines, s


def test_empty_file_safe():
    out = parse_source("empty.cpp", "")
    assert out.line_count == 0
    assert out.subroutines == []
    assert out.warnings == []


def test_header_with_only_declarations_returns_no_routines():
    # Forward declarations (`int foo(int);`) without bodies are NOT
    # routines. The v0 parser only records definitions.
    text = """\
#pragma once
int foo(int);
class Bar {
public:
    Bar();
    void method();
};
"""
    out = parse_source("decls.hpp", text)
    assert out.subroutines == [], out.subroutines


def test_overload_dedup_keeps_largest_body():
    # Two overloads of `add` — same qualified name (file-level `add`),
    # different parameter lists. v0 keeps the one with the larger body
    # span (matches Delphi's dedup behaviour).
    text = """\
int add(int a, int b) { return a + b; }
double add(double a, double b) {
    double r = a + b;
    /* extra line so the body is longer than the int overload */
    return r;
}
"""
    out = parse_source("overloads.cpp", text)
    names = [s.name for s in out.subroutines]
    assert names.count("add") == 1
    add = [s for s in out.subroutines if s.name == "add"][0]
    assert add.line_end - add.line_start >= 2  # double-overload body is multi-line


def test_template_prefix_does_not_carry_to_next_routine():
    text = """\
template<typename T>
T templated_one(T x) { return x; }

int plain_two(int x) { return x + 1; }
"""
    out = parse_source("two.cpp", text)
    by_name = {s.name: s for s in out.subroutines}
    assert "template<typename T>" in by_name["templated_one"].signature
    assert "template<" not in by_name["plain_two"].signature
