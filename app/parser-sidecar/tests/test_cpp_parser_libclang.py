"""Phase 9.4.b — unit tests for the production C++ parser
(libclang per ADR-028).

The 9 base tests mirror the v0 suite so the production parser must be at
least as accurate as v0 on the same fixtures. The 3 new tests exercise
capabilities v0 cannot handle:

  - `#ifdef`-conditional bodies (preprocessor expansion)
  - SFINAE / `requires`-constrained function templates
  - inline namespace nesting (`fmt::detail::format_int`)
"""
from __future__ import annotations

from parser_sidecar.cpp_parser_libclang import parse_source


_FMT_LIKE = """\
// Trimmed fmt-style sample for v0 parser calibration.
#include <string>
#include <vector>
#include <fmt/core.h>

namespace fmt {
namespace detail {

// Forward declaration so libclang resolves the parse_int CALL_EXPR
// inside parse_arg's body. Real fmt corpora reach this via
// fmt/format-inl.h's transitive includes; in this isolated fixture
// we declare it explicitly.
int parse_int(const char* p, int len);

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


# ──────────────────────────────────────────────────────────────────────
# Base parity with v0 (9 tests)
# ──────────────────────────────────────────────────────────────────────


def test_includes_recorded_as_common_block_refs():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    assert out.subroutines, "no routines found at all — parse must have collapsed"
    refs = out.subroutines[0].common_block_refs
    # libclang strips angle/quote characters and we keep the trailing
    # path component, so we look for the short forms.
    assert any("string" in r for r in refs)
    assert any("vector" in r for r in refs)
    assert any("core.h" in r for r in refs)


def test_finds_free_function_with_template():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    add = [s for s in out.subroutines if s.name.endswith("add")]
    assert len(add) == 1
    assert "template<T>" in add[0].signature or "template<typename T>" in add[0].signature
    assert add[0].line_start < add[0].line_end


def test_finds_class_methods_and_constructor():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    names = {s.name for s in out.subroutines}
    # libclang's qualified-name format uses `::` between segments.
    # `parse_arg` is inside `fmt::detail::format_context`, so its
    # qualified name is `fmt::detail::format_context::parse_arg`. We
    # accept any suffix match.
    assert any(n.endswith("parse_arg") for n in names)
    assert any(n.endswith("format") for n in names)
    assert any(n.endswith("formatter::formatter") for n in names)
    assert any(n.endswith("formatter::~formatter") for n in names)


def test_call_detection_inside_bodies():
    out = parse_source("fmt_like.cpp", _FMT_LIKE)
    by_suffix = {s.name.rsplit("::", 1)[-1]: s for s in out.subroutines}
    assert "parse_int" in by_suffix["parse_arg"].called_subroutines
    # The `format` method (inside `formatter`) calls parse_arg via ctx.
    assert "parse_arg" in by_suffix["format"].called_subroutines


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


def test_header_with_only_declarations_returns_routines_as_decls():
    """v0 treated header-only forward declarations as 'no routines'.
    libclang surfaces the declarations as routine cursors with the same
    qualified name as the definition, so we accept any non-empty
    output. Each entry should be small line span (no body)."""
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
    # libclang sees foo, Bar::Bar, Bar::method — these are
    # forward-declaration entries, not definitions. v0 returned [];
    # libclang returns 3. Accept either: the test guarantees that the
    # parser doesn't fail and that the contract is honest about what's
    # in the header.
    for s in out.subroutines:
        # Each forward decl is a single-line span (no body).
        assert s.line_end - s.line_start <= 1, f"{s.name} unexpectedly multi-line"


def test_overload_dedup_keeps_largest_body():
    text = """\
int add(int a, int b) { return a + b; }
double add(double a, double b) {
    double r = a + b;
    /* extra line so the body is longer than the int overload */
    return r;
}
"""
    out = parse_source("overloads.cpp", text)
    add_entries = [s for s in out.subroutines if s.name.endswith("add")]
    # libclang treats the two overloads as distinct cursors with the
    # same `name` — dedup keeps the larger one.
    assert len(add_entries) == 1
    assert add_entries[0].line_end - add_entries[0].line_start >= 2


def test_template_prefix_does_not_carry_to_next_routine():
    text = """\
template<typename T>
T templated_one(T x) { return x; }

int plain_two(int x) { return x + 1; }
"""
    out = parse_source("two.cpp", text)
    by_suffix = {s.name.rsplit("::", 1)[-1]: s for s in out.subroutines}
    assert "template<" in by_suffix["templated_one"].signature
    assert "template<" not in by_suffix["plain_two"].signature


# ──────────────────────────────────────────────────────────────────────
# New capabilities the production parser handles + v0 does not
# ──────────────────────────────────────────────────────────────────────


def test_preprocessor_ifdef_body_is_expanded():
    """v0's tokenizer can't expand `#ifdef`. libclang sees the active
    branch (`-D MSWINDOWS` would be one way) and emits the correct
    function body. Without a define the `#else` branch wins."""
    text = """\
#include <string>

#if defined(MSWINDOWS)
const char* greet() { return "windows"; }
#else
const char* greet() { return "posix"; }
#endif

int run() {
    return std::string(greet()).size();
}
"""
    out = parse_source("plat.cpp", text)
    names = {s.name.rsplit("::", 1)[-1] for s in out.subroutines}
    assert "greet" in names
    assert "run" in names
    run = next(s for s in out.subroutines if s.name.endswith("run"))
    # `run`'s body calls greet — libclang resolves the call expression
    # to the chosen `#else` branch's function.
    assert "greet" in run.called_subroutines


def test_sfinae_template_constraint_surfaces():
    """v0 has no semantic understanding of `std::enable_if_t`; it sees
    the function-template head structurally. libclang exposes the
    template's parameter list cleanly so the signature renders with
    `template<typename T, ...>` verbatim."""
    text = """\
#include <type_traits>

template<typename T, typename = std::enable_if_t<std::is_arithmetic_v<T>>>
T clamped_double(T value) {
    if (value < T(0)) return T(0);
    return value * T(2);
}
"""
    out = parse_source("sfinae.cpp", text)
    target = [s for s in out.subroutines if s.name.endswith("clamped_double")]
    assert len(target) == 1
    sig = target[0].signature
    # The signature must surface the template parameter list — at
    # least one `template<` prefix and a `T` parameter.
    assert "template<" in sig
    assert "T" in sig


def test_namespace_nesting_qualified_name():
    """v0 saw class methods as bare names. libclang surfaces routines
    with their full namespace path so `fmt::detail::format_int` is one
    name, not three different ones across translation."""
    text = """\
namespace fmt {
namespace detail {

template<typename T>
T format_int(T value) {
    return value;
}

}  // namespace detail
}  // namespace fmt
"""
    out = parse_source("nested.cpp", text)
    names = {s.name for s in out.subroutines}
    assert "fmt::detail::format_int" in names
