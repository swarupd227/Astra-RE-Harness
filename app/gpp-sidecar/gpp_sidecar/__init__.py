"""Astra g++ sidecar — compiles C++ sources on demand and runs the resulting
binary with a stdin-encoded input vector. Used by the API's
CrossRuntimeValidator to drive a C++ reference binary alongside the generated
.NET / Java scaffold so the harness can assert behavioural equivalence
(Phase 9.1.f). Shell-out to GNU g++ (`-std=c++20 -O0`) so real fmt / STL
code compiles without modification."""

__version__ = "0.1.0"
