"""HTTP routes for the calculation. The React form calls exactly these paths."""
from __future__ import annotations

from fastapi import APIRouter, Depends

from app.models import CalculationInput, CalculationResult, SavedCalculation
from app.rates import InMemoryRates
from app.service import CalculationService

router = APIRouter(prefix="/api/calculations", tags=["calculations"])

_service = CalculationService(InMemoryRates())


def get_service() -> CalculationService:
    """The service the routes use. Tests replace it (app.dependency_overrides[get_service]) with one built on a fake
    port, so a route test never reaches a real table, file or outside service."""
    return _service


@router.post("/preview", response_model=CalculationResult)
def preview(data: CalculationInput, service: CalculationService = Depends(get_service)) -> CalculationResult:
    """Calculate without recording. @SpecClaim INV-1"""
    return service.calculate(data)


@router.post("", response_model=SavedCalculation, status_code=201)
def record(data: CalculationInput, service: CalculationService = Depends(get_service)) -> SavedCalculation:
    """Calculate and record. @SpecClaim SE-1"""
    return service.record(data)


@router.get("", response_model=list[SavedCalculation])
def recorded(service: CalculationService = Depends(get_service)) -> list[SavedCalculation]:
    return service.recorded()
