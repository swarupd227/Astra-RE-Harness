"""The business rules: calculate a total from the inputs, and keep the results that are recorded."""
from __future__ import annotations

from datetime import datetime, timezone

from app.models import CalculationInput, CalculationResult, SavedCalculation
from app.rates import RatePort

# Long terms earn a discount on the rate. @SpecClaim EC-1
LONG_TERM_YEARS = 20
LONG_TERM_FACTOR = 0.9


class CalculationService:
    def __init__(self, rates: RatePort) -> None:
        self._rates = rates
        self._saved: list[SavedCalculation] = []

    def calculate(self, data: CalculationInput) -> CalculationResult:
        """Total = amount / 1,000 x rate x term, with the long-term discount. @SpecClaim INV-1 @SpecClaim EC-1"""
        rate = self._rates.rate_for(data.category)
        if data.term >= LONG_TERM_YEARS:
            rate = rate * LONG_TERM_FACTOR
        total = round(data.amount / 1000 * rate * data.term, 2)
        return CalculationResult(category=data.category, term=data.term, amount=data.amount, rate=round(rate, 4), total=total)

    def record(self, data: CalculationInput) -> SavedCalculation:
        """Calculate and keep the result, as the form's "record" action did. @SpecClaim SE-1"""
        result = self.calculate(data)
        saved = SavedCalculation(**result.model_dump(), id=len(self._saved) + 1, recorded_at=datetime.now(timezone.utc))
        self._saved.append(saved)
        return saved

    def recorded(self) -> list[SavedCalculation]:
        return list(self._saved)
