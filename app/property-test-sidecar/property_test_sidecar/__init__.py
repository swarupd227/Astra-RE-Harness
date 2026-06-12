"""Astra property-test sidecar — Hypothesis-based generator + falsifier.

Reads a signed spec (spec/v1) plus a caller-supplied equivalence callback URL,
and exercises the harness's 4th gate (falsifying-input search) per ADR-029.
For each claim with `promptHints.inputs` generator hints, the sidecar derives
Hypothesis strategies, generates up to N inputs (default 100), and POSTs each
to the callback. If any input produces a callback response of {"agree": false}
the sidecar lets Hypothesis shrink to the minimal counterexample before
returning. Per-claim budget is 30s, per-spec budget 10min.
"""

__version__ = "0.1.0"
