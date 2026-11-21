"""The HTTP routes the React form calls."""
from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def test_preview_returns_the_total():
    response = client.post("/api/calculations/preview", json={"category": "A", "term": 10, "amount": 50000})
    assert response.status_code == 200
    assert response.json()["total"] > 0


def test_a_bad_input_is_a_422_naming_the_field():
    response = client.post("/api/calculations/preview", json={"category": "A", "term": 99, "amount": 1000})
    assert response.status_code == 422
    assert "term" in response.text


def test_record_then_list():
    created = client.post("/api/calculations", json={"category": "B", "term": 5, "amount": 1000})
    assert created.status_code == 201
    listed = client.get("/api/calculations").json()
    assert any(item["id"] == created.json()["id"] for item in listed)
