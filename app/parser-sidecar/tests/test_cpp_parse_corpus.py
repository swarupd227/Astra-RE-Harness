"""Corpus-mode C++ parsing: the files on disk, so includes resolve.

Per-file parsing of a `.cpp` whose class is declared in a header libclang
cannot see yields nothing from that file — clang drops a member
definition of an undeclared class. `parse_corpus` writes the corpus to a
temporary root, adds every header directory as an include path, and
parses each file on disk. These tests pin the behaviours the dependency
graph relies on: definitions appear, callees are qualified, shared state
is found, declarations defer to definitions elsewhere, and a missing
third-party header no longer ends the parse.
"""
from __future__ import annotations

from parser_sidecar.cpp_parser_libclang import parse_corpus, parse_source


_HEADER = """\
#pragma once
#include <string>

namespace app {

extern int counter;
int helper(int x);

struct Store {
    static int hits;
    void save(int v);
    int load() const;
    int external() const;                       // defined nowhere in the corpus
    int inline_get() const { return load() + 1; }
};

}  // namespace app
"""

_STORE_CPP = """\
#include "app/Store.hpp"
#include <vendor/missing.hpp>

namespace app {

int counter = 0;
int Store::hits = 0;

int helper(int x) { return x + 1; }

void Store::save(int v) {
    counter += v;
    hits++;
}

int Store::load() const {
    return helper(counter);
}

}  // namespace app
"""

_MAIN_CPP = """\
#include "app/Store.hpp"

int main() {
    app::Store s;
    s.save(1);
    return s.load();
}
"""

FILES = [
    ("include/app/Store.hpp", _HEADER),
    ("src/Store.cpp", _STORE_CPP),
    ("src/main.cpp", _MAIN_CPP),
]


def test_per_file_parse_cannot_see_definitions_whose_class_is_elsewhere():
    """Documents the failure corpus mode exists to fix: parsed alone, the
    .cpp yields none of its member definitions."""
    out = parse_source("src/Store.cpp", _STORE_CPP)
    assert "app::Store::save" not in {s.name for s in out.subroutines}


def test_corpus_parse_resolves_definitions_across_files():
    outs = parse_corpus(FILES)
    assert [o.filename for o in outs] == [f for f, _ in FILES]

    store = {s.name: s for s in outs[1].subroutines}
    assert {"app::helper", "app::Store::save", "app::Store::load"} <= set(store), set(store)
    assert all(s.is_definition for s in store.values())
    assert store["app::Store::load"].called_subroutines == ("app::helper",)
    assert set(store["app::Store::load"].common_block_refs) == {"app::counter"}
    assert set(store["app::Store::save"].common_block_refs) == {"app::counter", "app::Store::hits"}
    # Definition lines come from the .cpp, not the header declaration.
    assert store["app::Store::save"].line_start == 11

    main = {s.name: s for s in outs[2].subroutines}["main"]
    assert {"app::Store::save", "app::Store::load"} <= set(main.called_subroutines), main.called_subroutines


def test_missing_third_party_header_is_reported_but_does_not_end_the_parse():
    outs = parse_corpus(FILES)
    assert any("missing.hpp" in w for w in outs[1].warnings), outs[1].warnings
    # Everything below the bad include still parsed.
    assert "app::Store::load" in {s.name for s in outs[1].subroutines}


def test_declarations_defer_to_definitions_in_other_files():
    outs = parse_corpus(FILES)
    header = {s.name: s for s in outs[0].subroutines}
    for defined_elsewhere in ("app::Store::save", "app::Store::load", "app::helper"):
        assert defined_elsewhere not in header, header.keys()
    # Nothing defines external(): the declaration is all the corpus has.
    assert "app::Store::external" in header
    assert not header["app::Store::external"].is_definition
    # An inline body in the header is a definition and keeps its calls.
    assert header["app::Store::inline_get"].is_definition
    assert header["app::Store::inline_get"].called_subroutines == ("app::Store::load",)


def test_unsafe_paths_are_parsed_in_memory_and_keep_their_slot():
    outs = parse_corpus([
        ("../escape.cpp", "int f() { return 1; }\n"),
        ("C:/abs/win.cpp", "int w() { return 3; }\n"),
        ("ok.cpp", "int g() { return 2; }\n"),
    ])
    assert [s.name for s in outs[0].subroutines] == ["f"]
    assert [s.name for s in outs[1].subroutines] == ["w"]
    assert [s.name for s in outs[2].subroutines] == ["g"]


def test_headers_are_harvested_from_the_units_that_include_them():
    """A header reached by a translation unit is not parsed again on its
    own; its inline definitions come from that unit's AST. A header no
    unit reaches is parsed standalone."""
    orphan = """\
#pragma once
namespace app { struct Orphan { int get() const { return 1; } }; }
"""
    outs = parse_corpus(FILES + [("include/app/Orphan.hpp", orphan)], max_workers=2)
    header = {s.name: s for s in outs[0].subroutines}
    assert header["app::Store::inline_get"].called_subroutines == ("app::Store::load",)
    assert [s.name for s in outs[3].subroutines] == ["app::Orphan::get"]


def test_process_pool_and_inline_paths_agree():
    pooled = parse_corpus(FILES, max_workers=2)
    inline = parse_corpus(FILES, max_workers=1)
    assert [(o.filename, [s.name for s in o.subroutines]) for o in pooled] == \
        [(o.filename, [s.name for s in o.subroutines]) for o in inline]


def test_progress_is_reported_per_unit_and_reaches_the_total():
    ticks = []
    outs = parse_corpus(FILES, max_workers=2, on_progress=lambda done, total, cur: ticks.append((done, total, cur)))
    assert len(outs) == 3
    assert ticks, "no progress at all"
    assert all(t[1] == 3 for t in ticks)
    assert [t[0] for t in ticks] == sorted(t[0] for t in ticks)
    assert ticks[-1][0] == 3
    assert {t[2] for t in ticks} <= {"src/Store.cpp", "src/main.cpp", "include/app/Store.hpp"}


def test_empty_file_keeps_its_slot():
    outs = parse_corpus([("empty.cpp", ""), ("a.cpp", "int a() { return 0; }\n")])
    assert outs[0].subroutines == [] and outs[0].line_count == 0 and outs[0].filename == "empty.cpp"
    assert [s.name for s in outs[1].subroutines] == ["a"]


def test_exceptions_no_longer_blind_a_body():
    """`-fno-exceptions` made clang reject every throw/try and drop the
    statement around it; the calls inside vanished with it."""
    text = """\
#include <stdexcept>
int risky(int x);
int guarded(int x) {
    try {
        return risky(x);
    } catch (const std::exception&) {
        throw std::runtime_error("bad");
    }
}
"""
    out = parse_source("guarded.cpp", text)
    guarded = {s.name: s for s in out.subroutines}["guarded"]
    assert "risky" in guarded.called_subroutines, guarded.called_subroutines
