"""The contract entry point takes the contract's field names and returns its outputs, from the same service the routes use."""
from app.contract import run


def test_run_takes_the_contract_fields_and_returns_the_contract_outputs():
    out = run({"category": "A", "term": 10, "amount": 50000})
    assert out == {"rate": 1.25, "total": 625.0}


def test_run_uses_the_long_term_rule_like_the_routes():
    out = run({"category": "A", "term": 20, "amount": 1000})
    assert out["rate"] == 1.125
