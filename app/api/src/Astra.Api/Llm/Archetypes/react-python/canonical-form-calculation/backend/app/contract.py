"""The package's front door in the workbook's own terms (the signed spec's workbook_cell_contracts claim).

`run` takes the contract's input fields by name (a Yes/No selection list as the list of ids that are marked), runs the
same service the routes use, and returns the contract's output fields by name. The workbook equivalence gate calls it
with the workbook's values case by case; keep every name exactly as the contract states it.
"""
from __future__ import annotations

from app.api import get_service
from app.models import CalculationInput


def run(inputs: dict) -> dict:
    """Contract inputs in, contract outputs out. @SpecClaim WCC-1"""
    data = CalculationInput(category=inputs["category"], term=inputs["term"], amount=inputs["amount"])
    result = get_service().calculate(data)
    return {"rate": result.rate, "total": result.total}
