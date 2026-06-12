"""Astra fpc sidecar — compiles Delphi / Object-Pascal sources on demand and
runs the resulting binary with a JSON-encoded input vector. Used by the API's
CrossRuntimeValidator to drive a Delphi reference binary alongside the
generated C# / Java scaffold so the harness can assert behavioural equivalence
(Phase 9.0.f). Shell-out to Free Pascal Compiler (fpc) in -Mdelphi mode so
real Indy / JCL / VCL idioms compile without modification."""

__version__ = "0.1.0"
