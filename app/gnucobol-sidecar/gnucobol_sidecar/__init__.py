"""Astra gnucobol sidecar — compiles COBOL sources on demand and runs the
resulting binary with a stdin-driven canonical input vector. Used by the
API's per-routine equivalence harness (Phase 5.6) to drive a COBOL
reference binary alongside the generated Java / .NET scaffolds so the
harness can assert behavioural equivalence at COBOL precision."""

__version__ = "0.1.0"
