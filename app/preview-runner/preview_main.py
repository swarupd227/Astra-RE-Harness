"""Serve a generated package as one app: its FastAPI routes under /api, its built React form everywhere else."""
from pathlib import Path

from fastapi.staticfiles import StaticFiles

from app.main import app

_dist = Path(__file__).resolve().parent.parent / "frontend" / "dist"
# Mounted last, so every API route the package defines wins over the static files.
app.mount("/", StaticFiles(directory=_dist, html=True), name="preview-ui")
