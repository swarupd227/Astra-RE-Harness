"""The HTTP routes the React form calls, run against a service on a fake port (never the real one)."""
import pytest
from fastapi.testclient import TestClient

from app.api import get_service
from app.main import app
from app.rates import InMemoryRates
from app.service import CalculationService


@pytest.fixture
def client():
    service = CalculationService(InMemoryRates({"A": 1.25, "B": 2.10}))
    app.dependency_overrides[get_service] = lambda: service
    yield TestClient(app)
    app.dependency_overrides.clear()


def test_preview_returns_the_total(client):
    response = client.post("/api/calculations/preview", json={"category": "A", "term": 10, "amount": 50000})
    assert response.status_code == 200
    assert response.json()["total"] == 625.0


def test_a_bad_input_is_a_422_naming_the_field(client):
    response = client.post("/api/calculations/preview", json={"category": "A", "term": 99, "amount": 1000})
    assert response.status_code == 422
    assert "term" in response.text


def test_record_then_list(client):
    created = client.post("/api/calculations", json={"category": "B", "term": 5, "amount": 1000})
    assert created.status_code == 201
    listed = client.get("/api/calculations").json()
    assert any(item["id"] == created.json()["id"] for item in listed)
