"""Astra gfortran sidecar — compiles Fortran sources on demand and runs the
resulting binary with a JSON-encoded input vector. Used by the API's
CrossRuntimeValidator to drive a Fortran reference binary alongside the
generated C# scaffold so the harness can assert behavioural equivalence."""

__version__ = "0.1.0"
