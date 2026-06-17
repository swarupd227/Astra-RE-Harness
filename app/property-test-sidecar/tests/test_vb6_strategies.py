"""
Smoke tests for the VB6 Hypothesis strategies (Phase 10.0.g).

Verifies each strategy produces JSON-serialisable values with the
expected shape so the property-test sidecar's callback contract
stays language-agnostic.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from decimal import Decimal

from hypothesis import given, settings
from hypothesis import strategies as st

from property_test_sidecar.vb6_strategies import (
    CURRENCY_MAX,
    CURRENCY_MIN,
    currency_strategy,
    date_strategy,
    dispatch_strategy,
    recordset_strategy,
    variant_strategy,
)


# ────────────────────────────────────────────────────────────────────────
# Variant
# ────────────────────────────────────────────────────────────────────────


@given(variant_strategy())
@settings(max_examples=50, deadline=None)
def test_variant_envelope_has_type_and_value(v):
    assert isinstance(v, dict)
    assert set(v.keys()) == {"type", "value"}
    assert v["type"] in ("Long", "Currency", "Double", "String", "Date", "Null")
    # Must be JSON-serialisable.
    json.dumps(v)


@given(variant_strategy(variant_of=["long"]))
@settings(max_examples=20, deadline=None)
def test_variant_subset_long_only(v):
    assert v["type"] == "Long"
    assert isinstance(v["value"], int)


@given(variant_strategy(variant_of=["null"]))
@settings(max_examples=5, deadline=None)
def test_variant_subset_null_only(v):
    assert v == {"type": "Null", "value": None}


@given(variant_strategy(variant_of=["string"], max_len=8, alphabet="ABC"))
@settings(max_examples=20, deadline=None)
def test_variant_string_respects_alphabet_and_maxlen(v):
    assert v["type"] == "String"
    assert isinstance(v["value"], str)
    assert len(v["value"]) <= 8
    assert all(c in "ABC" for c in v["value"])


# ────────────────────────────────────────────────────────────────────────
# Currency
# ────────────────────────────────────────────────────────────────────────


@given(currency_strategy())
@settings(max_examples=50, deadline=None)
def test_currency_emits_decimal_string_with_4_places(s):
    assert isinstance(s, str)
    # Decimal-parseable, within VB6 Currency range, exactly 4 decimal places.
    d = Decimal(s)
    assert d >= CURRENCY_MIN
    assert d <= CURRENCY_MAX
    if "." in s:
        assert len(s.split(".")[1]) == 4
    json.dumps(s)


@given(currency_strategy(min_value=0, max_value=100))
@settings(max_examples=20, deadline=None)
def test_currency_bounds_are_honoured(s):
    d = Decimal(s)
    assert d >= Decimal("0")
    assert d <= Decimal("100")


# ────────────────────────────────────────────────────────────────────────
# Date
# ────────────────────────────────────────────────────────────────────────


@given(date_strategy())
@settings(max_examples=20, deadline=None)
def test_date_is_iso_string_in_range(s):
    assert isinstance(s, str)
    # YYYY-MM-DD
    assert len(s) == 10
    assert s[4] == "-" and s[7] == "-"
    year = int(s[:4])
    assert 1900 <= year <= 2099
    json.dumps(s)


# ────────────────────────────────────────────────────────────────────────
# Recordset
# ────────────────────────────────────────────────────────────────────────


@given(recordset_strategy(
    columns=[
        {"name": "Id", "type": "long", "min": 1, "max": 1000},
        {"name": "Name", "type": "string", "maxLen": 16},
    ],
    min_rows=2,
    max_rows=5,
))
@settings(max_examples=10, deadline=None)
def test_recordset_emits_list_of_typed_dicts(rs):
    assert isinstance(rs, list)
    assert 2 <= len(rs) <= 5
    for row in rs:
        assert isinstance(row, dict)
        assert set(row.keys()) == {"Id", "Name"}
        assert isinstance(row["Id"], int)
        assert 1 <= row["Id"] <= 1000
        assert isinstance(row["Name"], str)
        assert len(row["Name"]) <= 16
    json.dumps(rs)


def test_recordset_empty_columns_returns_empty_list_strategy():
    rs = recordset_strategy(columns=[]).example()
    assert rs == []


@given(recordset_strategy(
    columns=[
        {"name": "Total", "type": "currency", "min": 0, "max": 9999},
        {"name": "PostedAt", "type": "date"},
    ],
    min_rows=0,
    max_rows=3,
))
@settings(max_examples=10, deadline=None)
def test_recordset_mixed_columns_include_currency_and_date(rs):
    for row in rs:
        assert isinstance(row["Total"], str)
        # Currency comes as decimal string.
        Decimal(row["Total"])
        assert isinstance(row["PostedAt"], str)
        assert len(row["PostedAt"]) == 10
    json.dumps(rs)


# ────────────────────────────────────────────────────────────────────────
# Dispatch
# ────────────────────────────────────────────────────────────────────────


@given(dispatch_strategy(members={
    "Visible": "bool",
    "Workbooks": "long",
    "Caption": "string",
}))
@settings(max_examples=10, deadline=None)
def test_dispatch_produces_typed_member_dict(proxy):
    assert isinstance(proxy, dict)
    assert set(proxy.keys()) == {"Visible", "Workbooks", "Caption"}
    assert isinstance(proxy["Visible"], bool)
    assert isinstance(proxy["Workbooks"], int)
    assert isinstance(proxy["Caption"], str)
    json.dumps(proxy)


def test_dispatch_empty_members_returns_empty_dict():
    proxy = dispatch_strategy(members={}).example()
    assert proxy == {}


# ────────────────────────────────────────────────────────────────────────
# Server-level wiring: _strategy_for delegates to vb6_strategies
# ────────────────────────────────────────────────────────────────────────


def test_server_strategy_for_dispatches_variant():
    from property_test_sidecar.server import InputHint, _strategy_for
    h = InputHint(name="x", type="variant")
    s = _strategy_for(h)
    v = s.example()
    assert isinstance(v, dict)
    assert v["type"] in ("Long", "Currency", "Double", "String", "Date", "Null")


def test_server_strategy_for_dispatches_currency():
    from property_test_sidecar.server import InputHint, _strategy_for
    h = InputHint(name="amount", type="currency", min=0, max=1000)
    s = _strategy_for(h)
    v = s.example()
    d = Decimal(v)
    assert 0 <= d <= 1000


def test_server_strategy_for_dispatches_recordset():
    from property_test_sidecar.server import InputHint, _strategy_for
    h = InputHint(
        name="lines",
        type="recordset",
        columns=[{"name": "qty", "type": "long", "min": 1, "max": 100}],
        minRows=1,
        maxRows=3,
    )
    s = _strategy_for(h)
    rows = s.example()
    assert 1 <= len(rows) <= 3
    for r in rows:
        assert isinstance(r["qty"], int)


def test_server_strategy_for_dispatches_dispatch():
    from property_test_sidecar.server import InputHint, _strategy_for
    h = InputHint(name="obj", type="dispatch", members={"Visible": "bool"})
    s = _strategy_for(h)
    proxy = s.example()
    assert isinstance(proxy["Visible"], bool)


def test_server_strategy_for_universal_types_unchanged():
    """Sanity: the non-VB6 types still route to the original branches."""
    from property_test_sidecar.server import InputHint, _strategy_for
    for type_token in ("int", "float", "bool", "bytes", "string"):
        h = InputHint(name="x", type=type_token)
        s = _strategy_for(h)
        # Just produce one value; if the strategy is wrong this throws.
        s.example()
