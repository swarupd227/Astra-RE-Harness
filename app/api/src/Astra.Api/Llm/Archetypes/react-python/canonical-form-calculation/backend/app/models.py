"""Request and result shapes. Input rules live here, so a bad value is refused before any rule runs.

@SpecClaim INV-1
"""
from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field

Category = Literal["A", "B", "C"]


class CalculationInput(BaseModel):
    """The inputs a user fills in on the form. @SpecClaim INV-1"""

    category: Category = Field(description="Which rate band applies.")
    term: int = Field(ge=1, le=40, description="Term in whole years.")
    amount: float = Field(gt=0, description="The amount the calculation is for.")


class CalculationResult(BaseModel):
    """What the calculation gives back: the rate it used and the total."""

    category: Category
    term: int
    amount: float
    rate: float
    total: float


class SavedCalculation(CalculationResult):
    """A result that has been recorded, with when it was recorded."""

    id: int
    recorded_at: datetime
