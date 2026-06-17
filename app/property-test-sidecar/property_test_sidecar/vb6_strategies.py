"""
VB6-specific Hypothesis strategies for the property-test sidecar.

Per Phase 10.0.g + the vb6.schema.json `generatorHints` taxonomy, the
4th gate exercises five extra input shapes that don't exist in the
Fortran / Delphi / C++ corpora:

    variant     — VB6 Variant: mixes Long / Currency / Double / String /
                  Date / Null per VB6 runtime coercion rules. The trap
                  most-extracted by the vb6-variant-plus-vs-amp-coercion
                  Golden Dataset entry.

    currency    — VB6 Currency: 64-bit fixed-point, 4 decimal places,
                  range ±922,337,203,685,477.5807. Strings carry the
                  exact decimal form to avoid float-precision drift.

    date        — VB6 Date: serialised as ISO-8601 string in the
                  callback payload (the runtime stores as Double-days-
                  from-1899-12-30, but the JSON callback contract
                  uses strings so cross-runtime byte-compare stays
                  language-agnostic).

    recordset   — In-memory ADO-shaped recordset: list of row dicts
                  keyed by column name. The hint's `columns` field
                  drives the per-column generators; row count is
                  bounded by `min` / `max`.

    dispatch    — COM IDispatch proxy: a dict mapping method/property
                  names to canned return values. The callback uses
                  this to mock late-bound dispatch sites without
                  needing a real COM runtime. The hint's `members`
                  field drives the keyspace.

The shapes produced by these strategies cross the callback boundary
as JSON — so all values are JSON-serialisable. Currency uses string
encoding (decimal form) to preserve precision; Date uses ISO-8601;
Variant emits a `{ "type": "...", "value": ... }` envelope so the
callback can route by runtime type without inferring.
"""
from __future__ import annotations

from decimal import Decimal
from typing import Optional

from hypothesis import strategies as st

# VB6 Currency range — 64-bit fixed-point, 4 decimal places.
# Range: -922,337,203,685,477.5808 .. +922,337,203,685,477.5807
CURRENCY_MIN = Decimal("-922337203685477.5808")
CURRENCY_MAX = Decimal("922337203685477.5807")

# VB6 Date range — broad enough to cover any realistic legacy data, but
# avoid the wraparound corners (Date is stored as Double days from
# 1899-12-30; pre-1900 dates use a different sign convention that's
# rarely exercised in real corpora).
DATE_MIN = "1900-01-01"
DATE_MAX = "2099-12-31"


def variant_strategy(
    min_value: Optional[float] = None,
    max_value: Optional[float] = None,
    max_len: Optional[int] = None,
    alphabet: Optional[str] = None,
    variant_of: Optional[list[str]] = None,
) -> st.SearchStrategy:
    """Strategy that produces a JSON-serialisable envelope
    `{ "type": "<vb6-runtime-type>", "value": <json-value> }` chosen
    uniformly from the named subset of Variant subtypes.

    Each subtype's bounds default to its idiomatic range; the caller
    can narrow Long / Double via `min_value` / `max_value` and String
    via `max_len` / `alphabet`.

    The Null branch produces `{ "type": "Null", "value": null }` so
    the callback can route the IsNull(value) check explicitly — VB6
    Variant Null is NOT the same as missing, and the callback must
    NOT collapse it to JSON's missing-field semantics.
    """
    subtypes = variant_of or ["long", "currency", "double", "string", "date", "null"]

    branches: list[st.SearchStrategy] = []
    if "long" in subtypes:
        lo = int(min_value) if min_value is not None else -(2**31)
        hi = int(max_value) if max_value is not None else (2**31) - 1
        branches.append(
            st.integers(min_value=lo, max_value=hi).map(
                lambda v: {"type": "Long", "value": v}
            )
        )
    if "currency" in subtypes:
        branches.append(
            currency_strategy(min_value, max_value).map(
                lambda v: {"type": "Currency", "value": v}
            )
        )
    if "double" in subtypes:
        lo = float(min_value) if min_value is not None else -1e9
        hi = float(max_value) if max_value is not None else  1e9
        branches.append(
            st.floats(
                min_value=lo, max_value=hi,
                allow_nan=False, allow_infinity=False,
            ).map(lambda v: {"type": "Double", "value": v})
        )
    if "string" in subtypes:
        n = max_len if max_len is not None else 32
        if alphabet:
            text_s = st.text(alphabet=alphabet, min_size=0, max_size=n)
        else:
            text_s = st.text(min_size=0, max_size=n)
        branches.append(text_s.map(lambda v: {"type": "String", "value": v}))
    if "date" in subtypes:
        branches.append(date_strategy().map(lambda v: {"type": "Date", "value": v}))
    if "null" in subtypes:
        branches.append(st.just({"type": "Null", "value": None}))

    if not branches:
        # Defensive: empty subset → behave like the broad mix.
        return variant_strategy()
    return st.one_of(*branches)


def currency_strategy(
    min_value: Optional[float] = None,
    max_value: Optional[float] = None,
) -> st.SearchStrategy:
    """Strategy producing VB6 Currency values as decimal strings.

    Encoding as String (e.g. "12.3400") preserves the Currency type's
    exact 4-decimal-place representation across the JSON callback
    boundary — Python floats would silently drift.
    """
    lo = Decimal(str(min_value)) if min_value is not None else CURRENCY_MIN
    hi = Decimal(str(max_value)) if max_value is not None else CURRENCY_MAX

    # Generate as a Decimal with the same 4-place precision the VB6
    # runtime enforces; emit as a fixed-point string.
    return st.decimals(
        min_value=lo, max_value=hi,
        places=4, allow_nan=False, allow_infinity=False,
    ).map(lambda d: format(d, "f"))


def date_strategy() -> st.SearchStrategy:
    """Strategy producing VB6 Date values as ISO-8601 date strings
    (YYYY-MM-DD)."""
    from datetime import date as _date
    return st.dates(
        min_value=_date(1900, 1, 1),
        max_value=_date(2099, 12, 31),
    ).map(lambda d: d.isoformat())


def recordset_strategy(
    columns: list[dict],
    min_rows: int = 0,
    max_rows: int = 10,
) -> st.SearchStrategy:
    """Strategy producing an in-memory ADO-shaped recordset — a list
    of row dicts keyed by column name.

    `columns` is a list of `{"name": str, "type": str, ...per-column-hint}`
    objects. Per-column-hint accepts the same min/max/maxLen/alphabet
    fields as the top-level InputHint plus variant `variant_of`.

    The callback receives the rows as a JSON array of objects; it can
    walk them as if they were a DAO Recordset (Fields[\"x\"].Value
    reads a column; .MoveNext is iteration). This lets the 4th gate
    exercise the DAO patterns in the
    vb6-dao-recordcount-movelast-movefirst Golden Dataset entry
    without needing a real Jet runtime in the property-test container.
    """
    if not columns:
        return st.just([])

    row_strategy_fields: dict[str, st.SearchStrategy] = {}
    for col in columns:
        col_type = (col.get("type") or "string").lower()
        col_strategy = _column_strategy(col_type, col)
        row_strategy_fields[col["name"]] = col_strategy
    row_strategy = st.fixed_dictionaries(row_strategy_fields)

    return st.lists(row_strategy, min_size=min_rows, max_size=max_rows)


def dispatch_strategy(
    members: dict[str, str],
) -> st.SearchStrategy:
    """Strategy producing a COM IDispatch proxy as a dict mapping
    member names to canned return values.

    `members` is a `{ "MemberName": "<type-token>" }` dict where the
    type token is one of {`long`, `currency`, `double`, `string`,
    `bool`, `variant`, `recordset`, `null`}. The callback consumes
    the proxy by name — `proxy["Workbooks"]` returns the canned
    recordset, `proxy["Visible"]` returns the canned bool, etc.

    Used for testing routines that take an `Object` or `Variant`
    parameter and dispatch methods on it (the
    vb6-late-bound-excel-create-object trap).
    """
    if not members:
        return st.just({})

    per_member: dict[str, st.SearchStrategy] = {}
    for name, type_token in members.items():
        per_member[name] = _column_strategy(type_token.lower(), {})
    return st.fixed_dictionaries(per_member)


# ────────────────────────────────────────────────────────────────────────
# Helpers
# ────────────────────────────────────────────────────────────────────────


def _column_strategy(type_token: str, hint: dict) -> st.SearchStrategy:
    """Per-column / per-member strategy lookup. Mirrors the top-level
    `_strategy_for` switch but accepts a dict hint and stays inside
    this module so it doesn't drag the main server's switch with it."""
    t = type_token.lower()
    if t in ("long", "int", "integer", "i32", "i64"):
        lo = int(hint["min"]) if hint.get("min") is not None else -(2**31)
        hi = int(hint["max"]) if hint.get("max") is not None else (2**31) - 1
        return st.integers(min_value=lo, max_value=hi)
    if t == "currency":
        return currency_strategy(hint.get("min"), hint.get("max"))
    if t in ("double", "float", "real"):
        lo = float(hint["min"]) if hint.get("min") is not None else -1e9
        hi = float(hint["max"]) if hint.get("max") is not None else  1e9
        return st.floats(
            min_value=lo, max_value=hi,
            allow_nan=False, allow_infinity=False,
        )
    if t in ("string", "str", "text", "char"):
        n = hint.get("maxLen") or hint.get("max_len") or 32
        if hint.get("alphabet"):
            return st.text(alphabet=hint["alphabet"], min_size=0, max_size=n)
        return st.text(min_size=0, max_size=n)
    if t in ("bool", "boolean"):
        return st.booleans()
    if t == "date":
        return date_strategy()
    if t == "variant":
        return variant_strategy(
            hint.get("min"), hint.get("max"),
            hint.get("maxLen") or hint.get("max_len"),
            hint.get("alphabet"),
            hint.get("variantOf") or hint.get("variant_of"),
        )
    if t == "null":
        return st.just(None)
    if t == "recordset":
        return recordset_strategy(
            hint.get("columns") or [],
            int(hint.get("minRows") or hint.get("min_rows") or 0),
            int(hint.get("maxRows") or hint.get("max_rows") or 10),
        )
    if t == "dispatch":
        return dispatch_strategy(hint.get("members") or {})
    # Unknown token: fall back to a short text so the callback path
    # still exercises.
    return st.text(min_size=0, max_size=16)
