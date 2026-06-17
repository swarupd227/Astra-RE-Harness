"""Astra vb6-sidecar — VB6 compile + run as a sidecar service.

Per ADR-037 the production deployment runs on Windows Server Core 2022
with customer-provided VB6 runtime DLLs at /runtime. The Wine-based
dev fallback covers non-COM routines for the Linux-first dev team.
"""

__version__ = "0.1.0"
