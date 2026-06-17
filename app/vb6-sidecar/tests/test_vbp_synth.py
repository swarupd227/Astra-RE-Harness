"""
Smoke tests for the platform-independent pieces of vb6_sidecar.server.

The compile + run paths need vb6.exe + Wine/Windows runtime — those
live in the integration suite that runs in the container. These tests
cover the .vbp synthesis, runtime-DLL preflight, and tier resolution
logic that we can exercise on any host.
"""
from __future__ import annotations

import sys
import tempfile
from pathlib import Path

# Make the sidecar package importable without installing it.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from fastapi import HTTPException
from vb6_sidecar.server import (
    REQUIRED_RUNTIME,
    Vb6Source,
    _check_runtime,
    _looks_like_com_dispatch_error,
    _resolve_main_project,
)


def test_synth_vbp_from_bas_only():
    with tempfile.TemporaryDirectory() as td:
        build = Path(td)
        bas = build / "modOrders.bas"
        bas.write_text("Attribute VB_Name = \"modOrders\"\n", encoding="utf-8")

        vbp = _resolve_main_project([bas], build, requested=None, link_as="executable")

        assert vbp.exists()
        assert vbp.name == "AstraDriver.vbp"
        body = vbp.read_text()
        assert "Type=Exe" in body
        assert "Module=modOrders; modOrders.bas" in body


def test_synth_vbp_from_mixed_module_class_form():
    with tempfile.TemporaryDirectory() as td:
        build = Path(td)
        bas = build / "modOrders.bas"
        cls = build / "clsCustomer.cls"
        frm = build / "frmOrderEntry.frm"
        bas.write_text("Attribute VB_Name = \"modOrders\"\n", encoding="utf-8")
        cls.write_text("VERSION 1.0 CLASS\n", encoding="utf-8")
        frm.write_text("VERSION 5.00\n", encoding="utf-8")

        vbp = _resolve_main_project([bas, cls, frm], build, None, "executable")

        body = vbp.read_text()
        assert "Module=modOrders; modOrders.bas" in body
        assert "Class=clsCustomer; clsCustomer.cls" in body
        assert "Form=frmOrderEntry.frm" in body


def test_synth_vbp_honours_caller_supplied_vbp():
    with tempfile.TemporaryDirectory() as td:
        build = Path(td)
        bas = build / "modOrders.bas"
        vbp = build / "MyProj.vbp"
        bas.write_text("Attribute VB_Name = \"modOrders\"\n", encoding="utf-8")
        vbp.write_text("Type=Exe\nModule=modOrders; modOrders.bas\n", encoding="utf-8")

        result = _resolve_main_project([bas, vbp], build, "MyProj.vbp", "executable")

        assert result == vbp
        assert "MyProj" in result.read_text() or result.name == "MyProj.vbp"


def test_synth_vbp_picks_first_existing_vbp_when_none_requested():
    with tempfile.TemporaryDirectory() as td:
        build = Path(td)
        vbp = build / "Existing.vbp"
        vbp.write_text("Type=Exe\n", encoding="utf-8")

        result = _resolve_main_project([vbp], build, None, "executable")

        assert result == vbp


def test_synth_vbp_rejects_unknown_requested_main():
    with tempfile.TemporaryDirectory() as td:
        build = Path(td)
        bas = build / "modOrders.bas"
        bas.write_text("Attribute VB_Name = \"modOrders\"\n", encoding="utf-8")

        try:
            _resolve_main_project([bas], build, "Nope.vbp", "executable")
        except HTTPException as ex:
            assert ex.status_code == 400
            assert "Nope.vbp" in ex.detail
        else:
            raise AssertionError("expected HTTPException for missing main project")


def test_synth_vbp_rejects_non_executable_link_when_no_vbp():
    with tempfile.TemporaryDirectory() as td:
        build = Path(td)
        bas = build / "modOrders.bas"
        bas.write_text("Attribute VB_Name = \"modOrders\"\n", encoding="utf-8")

        try:
            _resolve_main_project([bas], build, None, "library")
        except HTTPException as ex:
            assert ex.status_code == 400
            assert "executable" in ex.detail
        else:
            raise AssertionError("expected HTTPException for non-executable link")


def test_check_runtime_reports_missing_when_dir_empty():
    with tempfile.TemporaryDirectory() as td:
        import vb6_sidecar.server as srv
        original = srv.RUNTIME_DIR
        try:
            srv.RUNTIME_DIR = Path(td)
            ok, missing = _check_runtime()
            assert not ok
            assert set(missing) == set(REQUIRED_RUNTIME)
        finally:
            srv.RUNTIME_DIR = original


def test_check_runtime_reports_ready_when_artifacts_present():
    with tempfile.TemporaryDirectory() as td:
        for fname in REQUIRED_RUNTIME:
            (Path(td) / fname).write_bytes(b"")
        import vb6_sidecar.server as srv
        original = srv.RUNTIME_DIR
        try:
            srv.RUNTIME_DIR = Path(td)
            ok, missing = _check_runtime()
            assert ok
            assert missing == []
        finally:
            srv.RUNTIME_DIR = original


def test_com_dispatch_heuristic_fires_on_known_phrases():
    assert _looks_like_com_dispatch_error("", "ActiveX component can't create object")
    assert _looks_like_com_dispatch_error("automation error", "")
    assert _looks_like_com_dispatch_error("OLE error 0x80004005", "")
    assert _looks_like_com_dispatch_error("", "CreateObject failed")


def test_com_dispatch_heuristic_silent_on_normal_run():
    assert not _looks_like_com_dispatch_error("hello world", "")
    assert not _looks_like_com_dispatch_error("", "")
    assert not _looks_like_com_dispatch_error("42", "0 errors")
