"""
property-test sidecar — REST surface for Hypothesis-driven falsifying-input
search (ADR-029, Phase 9.0.g).

Endpoints
---------
GET  /health                       liveness probe

POST /falsify                      body: {
                                     "specId":     "<runId or specId>",
                                     "language":   "delphi" | "fortran-f77" | "cobol",
                                     "callbackUrl":"http://api:8080/internal/equiv",
                                     "claims": [
                                       { "claimId": "INV-1",
                                         "generatorHints": {
                                           "inputs": [
                                             { "name":"x", "type":"int",   "min":-1e6, "max":1e6 },
                                             { "name":"s", "type":"string","maxLen":64 }
                                           ],
                                           "constraint": null,
                                           "examples":   [ { "x":0, "s":"" } ]
                                         },
                                         "budget":   100,
                                         "timeoutMs":30000
                                       }
                                     ],
                                     "perSpecTimeoutMs": 600000
                                   }
                                   -> {
                                     "specId":       "...",
                                     "claimResults": [
                                       { "claimId":"INV-1", "examplesTried":17,
                                         "falsifying":{"x":-1,"s":""},
                                         "refOutput":"0", "candOutput":"1",
                                         "elapsedMs":1832, "timedOut":false }
                                     ],
                                     "overallFalsified": true,
                                     "totalElapsedMs":   1900
                                   }

Equivalence callback contract
-----------------------------
For each generated input the sidecar POSTs:
    { "specId":"...", "claimId":"...", "input":{...} }
to `callbackUrl` and expects:
    { "agree": true|false,
      "refOutput":  "<string-encoded reference output>",
      "candOutput": "<string-encoded candidate output>" }

A non-200 callback response is treated as "agree: true" — the sidecar's job
is to find candidate ≠ reference, not to debug the harness wiring. Errors
are surfaced in the response's `callbackErrors` array so the API can flag
them in the run dashboard.
"""

from __future__ import annotations

import logging
import os
import time
from typing import Any, Optional

import httpx
from fastapi import FastAPI
from hypothesis import HealthCheck, Phase, given, settings, strategies as st
from hypothesis.errors import Unsatisfiable
from pydantic import BaseModel, Field

from property_test_sidecar import __version__

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-property","msg":"%(message)s"}',
)
log = logging.getLogger("astra.property")

DEFAULT_PER_CLAIM_BUDGET = 100
DEFAULT_PER_CLAIM_TIMEOUT_MS = 30_000
DEFAULT_PER_SPEC_TIMEOUT_MS = 600_000

app = FastAPI(title="Astra property-test sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Pydantic models
# ────────────────────────────────────────────────────────────────────────


class InputHint(BaseModel):
    name: str
    type: str = Field(..., description="One of: int | float | string | bool | bytes.")
    min: Optional[float] = None
    max: Optional[float] = None
    max_len: Optional[int] = Field(None, alias="maxLen")
    alphabet: Optional[str] = None

    class Config:
        populate_by_name = True


class GeneratorHints(BaseModel):
    inputs: list[InputHint] = Field(default_factory=list)
    constraint: Optional[str] = None
    examples: list[dict[str, Any]] = Field(default_factory=list)


class ClaimSpec(BaseModel):
    claim_id: str = Field(..., alias="claimId")
    generator_hints: GeneratorHints = Field(..., alias="generatorHints")
    budget: int = DEFAULT_PER_CLAIM_BUDGET
    timeout_ms: int = Field(DEFAULT_PER_CLAIM_TIMEOUT_MS, alias="timeoutMs")

    class Config:
        populate_by_name = True


class FalsifyRequest(BaseModel):
    spec_id: str = Field(..., alias="specId")
    language: str = "delphi"
    callback_url: str = Field(..., alias="callbackUrl")
    claims: list[ClaimSpec]
    per_spec_timeout_ms: int = Field(DEFAULT_PER_SPEC_TIMEOUT_MS, alias="perSpecTimeoutMs")

    class Config:
        populate_by_name = True


class ClaimResult(BaseModel):
    claim_id: str = Field(..., alias="claimId")
    examples_tried: int = Field(..., alias="examplesTried")
    falsifying: Optional[dict[str, Any]] = None
    ref_output: Optional[str] = Field(None, alias="refOutput")
    cand_output: Optional[str] = Field(None, alias="candOutput")
    elapsed_ms: int = Field(..., alias="elapsedMs")
    timed_out: bool = Field(..., alias="timedOut")
    callback_errors: int = Field(0, alias="callbackErrors")
    skip_reason: Optional[str] = Field(None, alias="skipReason")

    class Config:
        populate_by_name = True


class FalsifyResponse(BaseModel):
    spec_id: str = Field(..., alias="specId")
    claim_results: list[ClaimResult] = Field(..., alias="claimResults")
    overall_falsified: bool = Field(..., alias="overallFalsified")
    total_elapsed_ms: int = Field(..., alias="totalElapsedMs")

    class Config:
        populate_by_name = True


# ────────────────────────────────────────────────────────────────────────
# Routes
# ────────────────────────────────────────────────────────────────────────


@app.get("/health")
def health():
    import hypothesis as hyp
    return {
        "service": "astra-property-test",
        "version": __version__,
        "hypothesisVersion": hyp.__version__,
    }


@app.post("/falsify", response_model=FalsifyResponse, response_model_by_alias=True)
def falsify(req: FalsifyRequest) -> FalsifyResponse:
    t_spec = time.monotonic()
    deadline_s = t_spec + (req.per_spec_timeout_ms / 1000)

    results: list[ClaimResult] = []
    for claim in req.claims:
        if time.monotonic() >= deadline_s:
            results.append(ClaimResult(
                claim_id=claim.claim_id,
                examples_tried=0,
                falsifying=None, ref_output=None, cand_output=None,
                elapsed_ms=0, timed_out=True, callback_errors=0,
                skip_reason="per_spec_timeout_hit",
            ))
            continue
        result = _run_one_claim(req.spec_id, req.callback_url, claim)
        results.append(result)

    total_ms = int((time.monotonic() - t_spec) * 1000)
    overall_falsified = any(r.falsifying is not None for r in results)
    return FalsifyResponse(
        spec_id=req.spec_id,
        claim_results=results,
        overall_falsified=overall_falsified,
        total_elapsed_ms=total_ms,
    )


# ────────────────────────────────────────────────────────────────────────
# Workers
# ────────────────────────────────────────────────────────────────────────


def _run_one_claim(spec_id: str, callback_url: str, claim: ClaimSpec) -> ClaimResult:
    """Drive Hypothesis around a single claim's generator-hint strategy.

    The strategy yields a dict of {input_name: value}. For each generated
    input the inner function POSTs to the callback. If the callback replies
    `agree:false` Hypothesis records the falsifying example AND keeps
    shrinking to a minimal counterexample.
    """
    strategy = _build_strategy(claim.generator_hints)
    if strategy is None:
        return ClaimResult(
            claim_id=claim.claim_id,
            examples_tried=0,
            falsifying=None, ref_output=None, cand_output=None,
            elapsed_ms=0, timed_out=False, callback_errors=0,
            skip_reason="no_inputs_in_generator_hints",
        )

    t_claim = time.monotonic()
    deadline_s = t_claim + (claim.timeout_ms / 1000)

    counterexample: dict[str, Any] = {}
    captured_outputs: dict[str, str] = {}
    callback_errors = [0]
    examples_tried = [0]
    timed_out = [False]

    @settings(
        max_examples=claim.budget,
        deadline=None,  # we enforce the deadline ourselves
        suppress_health_check=[HealthCheck.too_slow, HealthCheck.function_scoped_fixture],
        phases=[Phase.generate, Phase.shrink],
        derandomize=True,  # reproducible runs given same spec
    )
    @given(payload=strategy)
    def property_holds(payload: dict[str, Any]) -> None:
        examples_tried[0] += 1
        if time.monotonic() >= deadline_s:
            timed_out[0] = True
            return  # silently stop — Hypothesis treats this as "no failure"
        try:
            with httpx.Client(timeout=10.0) as client:
                resp = client.post(callback_url, json={
                    "specId":  spec_id,
                    "claimId": claim.claim_id,
                    "input":   payload,
                })
            if resp.status_code != 200:
                callback_errors[0] += 1
                return
            body = resp.json()
        except Exception:
            callback_errors[0] += 1
            return
        if not body.get("agree", True):
            counterexample.clear()
            counterexample.update(payload)
            captured_outputs["ref"]  = str(body.get("refOutput", ""))[:2048]
            captured_outputs["cand"] = str(body.get("candOutput", ""))[:2048]
            assert False, f"claim {claim.claim_id} disagreement"

    try:
        property_holds()
        # No assertion fired — the property holds for all explored inputs.
        falsifying = None
        ref_out = None
        cand_out = None
    except AssertionError:
        # Hypothesis shrank to the minimal counterexample; the closure has it.
        falsifying = dict(counterexample)
        ref_out = captured_outputs.get("ref")
        cand_out = captured_outputs.get("cand")
    except Unsatisfiable:
        # All generator candidates filtered out by `assume` — strategy is dead.
        falsifying = None
        ref_out = None
        cand_out = None

    elapsed_ms = int((time.monotonic() - t_claim) * 1000)
    return ClaimResult(
        claim_id=claim.claim_id,
        examples_tried=examples_tried[0],
        falsifying=falsifying,
        ref_output=ref_out,
        cand_output=cand_out,
        elapsed_ms=elapsed_ms,
        timed_out=timed_out[0],
        callback_errors=callback_errors[0],
    )


def _build_strategy(hints: GeneratorHints) -> Optional[st.SearchStrategy]:
    """Translate `inputs` hints into a single Hypothesis dict strategy.

    Each input's `type` picks a base strategy; `min` / `max` / `maxLen` /
    `alphabet` narrow it. A claim with zero inputs is unrunnable (the
    sidecar returns `skipReason: no_inputs_in_generator_hints`).
    """
    if not hints.inputs:
        return None

    per_input: dict[str, st.SearchStrategy] = {}
    for inp in hints.inputs:
        per_input[inp.name] = _strategy_for(inp)
    return st.fixed_dictionaries(per_input)


def _strategy_for(inp: InputHint) -> st.SearchStrategy:
    t = inp.type.lower()
    if t in ("int", "integer", "i32", "i64"):
        lo = int(inp.min) if inp.min is not None else -(2**31)
        hi = int(inp.max) if inp.max is not None else  (2**31) - 1
        return st.integers(min_value=lo, max_value=hi)
    if t in ("float", "double", "real"):
        lo = float(inp.min) if inp.min is not None else -1e9
        hi = float(inp.max) if inp.max is not None else  1e9
        return st.floats(
            min_value=lo, max_value=hi,
            allow_nan=False, allow_infinity=False,
        )
    if t in ("bool", "boolean"):
        return st.booleans()
    if t in ("bytes", "binary"):
        max_len = inp.max_len if inp.max_len is not None else 64
        return st.binary(min_size=0, max_size=max_len)
    if t in ("string", "str", "text", "char"):
        max_len = inp.max_len if inp.max_len is not None else 64
        if inp.alphabet:
            return st.text(alphabet=inp.alphabet, min_size=0, max_size=max_len)
        return st.text(min_size=0, max_size=max_len)
    # Unknown type: fall back to text so the sidecar at least exercises
    # the callback path. The API can warn on the unknown hint shape.
    return st.text(min_size=0, max_size=16)


def main() -> None:
    import uvicorn
    port = int(os.environ.get("PROPERTY_TEST_PORT", "51056"))
    uvicorn.run(
        "property_test_sidecar.server:app",
        host="0.0.0.0",
        port=port,
        log_level="warning",
        access_log=False,
    )


if __name__ == "__main__":
    main()
