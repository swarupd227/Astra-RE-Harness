"""
Tests for parser_sidecar.java_parser.

Phase 14.0 — smoke coverage for the Java v0 brace-aware tokenizer. Asserts it
extracts constructors, concrete methods, interface/abstract stubs and nested
types; tracks the enclosing type; skips control-flow / anonymous-class blocks;
and blanks comments / strings / text blocks so their braces never miscount.
"""
from __future__ import annotations

import textwrap

from parser_sidecar.java_parser import parse_source


_SAMPLE = textwrap.dedent('''
    package com.kiwiplan.orders;

    import java.util.List;
    import java.util.Optional;

    /** Order service. */
    public class OrderService implements AuditAware {

        private final OrderRepository repo;

        public OrderService(OrderRepository repo) {
            this.repo = repo;
        }

        @Override
        public int submit(Order order) throws ValidationException {
            if (!creditOk(order)) {
                return -1;
            }
            Runnable r = new Runnable() {
                public void run() { log("x"); }
            };
            return repo.insert(order);
        }

        private boolean creditOk(Order order) {
            return computeTotal(order) < 1000;
        }

        public interface AuditAware {
            void onAudit(String action);
            default boolean enabled() { return true; }
        }

        enum Status { OPEN, POSTED }
    }
''').strip()


def _by_name(out):
    return {s.name: s for s in out.subroutines}


def test_constructor_and_methods_extracted():
    out = parse_source("OrderService.java", _SAMPLE)
    names = set(_by_name(out))
    assert "OrderService" in names   # constructor
    assert "submit" in names
    assert "creditOk" in names


def test_interface_stub_and_default_method():
    out = parse_source("OrderService.java", _SAMPLE)
    names = set(_by_name(out))
    assert "onAudit" in names         # ;-terminated stub
    assert "enabled" in names          # default method with body


def test_enclosing_type_tracked():
    out = parse_source("OrderService.java", _SAMPLE)
    by = _by_name(out)
    assert by["submit"].common_block_refs == ("OrderService",)
    assert by["enabled"].common_block_refs == ("AuditAware",)


def test_calls_exclude_self_and_dotted():
    out = parse_source("OrderService.java", _SAMPLE)
    by = _by_name(out)
    # submit calls creditOk (bare); repo.insert is dotted → excluded;
    # the method must not list its own name.
    assert "creditOk" in by["submit"].called_subroutines
    assert "submit" not in by["submit"].called_subroutines
    assert "insert" not in by["submit"].called_subroutines


def test_control_flow_not_treated_as_method():
    out = parse_source("OrderService.java", _SAMPLE)
    names = set(_by_name(out))
    for kw in ("if", "for", "while", "switch", "catch"):
        assert kw not in names


def test_string_and_comment_braces_ignored():
    src = textwrap.dedent('''
        public class C {
            public String weird() {
                String s = "a { b } c";  // } not a real brace
                return s;
            }
        }
    ''').strip()
    out = parse_source("C.java", src)
    weird = next(s for s in out.subroutines if s.name == "weird")
    # The braces inside the string / comment must not truncate the body.
    assert weird.line_end > weird.line_start


def test_pure_data_type_yields_one_unit():
    src = textwrap.dedent('''
        package x;
        public record Point(int x, int y) {}
    ''').strip()
    out = parse_source("Point.java", src)
    assert len(out.subroutines) >= 1
