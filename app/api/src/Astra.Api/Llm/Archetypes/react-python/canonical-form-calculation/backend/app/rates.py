"""Where the calculation's lookup values come from — the table a spreadsheet kept on a (hidden) sheet.

The service depends on the RatePort protocol, not on this in-memory table, so a database or a
file can replace it without touching a rule.
"""
from __future__ import annotations

from typing import Protocol


class RatePort(Protocol):
    def rate_for(self, category: str) -> float: ...


class InMemoryRates:
    """Rates per 1,000 of amount, by category. @SpecClaim SE-1"""

    def __init__(self, table: dict[str, float] | None = None) -> None:
        self._table = dict(table or {"A": 1.25, "B": 2.10, "C": 3.40})

    def rate_for(self, category: str) -> float:
        if category not in self._table:
            raise KeyError(f"No rate for category {category}")
        return self._table[category]
