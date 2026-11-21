"""HTTP routes for the calculation. The React form calls exactly these paths."""
from __future__ import annotations

from fastapi import APIRouter

from app.models import CalculationInput, CalculationResult, SavedCalculation
from app.rates import InMemoryRates
from app.service import CalculationService

router = APIRouter(prefix="/api/calculations", tags=["calculations"])
service = CalculationService(InMemoryRates())


@router.post("/preview", response_model=CalculationResult)
def preview(data: CalculationInput) -> CalculationResult:
    """Calculate without recording. @SpecClaim INV-1"""
    return service.calculate(data)


@router.post("", response_model=SavedCalculation, status_code=201)
def record(data: CalculationInput) -> SavedCalculation:
    """Calculate and record. @SpecClaim SE-1"""
    return service.record(data)


@router.get("", response_model=list[SavedCalculation])
def recorded() -> list[SavedCalculation]:
    return service.recorded()
