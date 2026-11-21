"""The business rules, without HTTP."""
import pytest
from pydantic import ValidationError

from app.models import CalculationInput
from app.rates import InMemoryRates
from app.service import CalculationService


def make() -> CalculationService:
    return CalculationService(InMemoryRates({"A": 2.0, "B": 3.0, "C": 4.0}))


def test_total_is_amount_per_thousand_times_rate_times_term():
    # @SpecClaim INV-1
    result = make().calculate(CalculationInput(category="A", term=10, amount=50_000))
    assert result.rate == 2.0
    assert result.total == 1000.0


def test_long_terms_get_the_discounted_rate():
    # @SpecClaim EC-1
    result = make().calculate(CalculationInput(category="B", term=20, amount=1000))
    assert result.rate == pytest.approx(2.7)
    assert result.total == pytest.approx(54.0)


def test_inputs_outside_their_rules_are_refused():
    # @SpecClaim INV-1
    with pytest.raises(ValidationError):
        CalculationInput(category="A", term=0, amount=1000)
    with pytest.raises(ValidationError):
        CalculationInput(category="Z", term=5, amount=1000)


def test_recording_keeps_the_result():
    # @SpecClaim SE-1
    service = make()
    saved = service.record(CalculationInput(category="C", term=5, amount=2000))
    assert saved.id == 1
    assert service.recorded() == [saved]
