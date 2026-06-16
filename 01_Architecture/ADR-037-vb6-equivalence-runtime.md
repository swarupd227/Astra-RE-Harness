# ADR-037 — VB6 equivalence runtime

**Status:** Draft (proposed)
**Phase:** 10.0.f (pre-kickoff)
**Companion:** `phase-10.0-vb6-source-language.md`, ADR-035 (parser choice), ADR-036 (target paradigm)

---

## Context

Phase 10.0.f ships `vb6-sidecar`, the equivalence runtime that runs the original VB6 source side-by-side with the generated .NET 10 candidate and byte-compares outputs. This is Gate 3 of the 4-gate validation — without it, we lose the strongest behaviour-parity signal in the harness.

VB6 is **Windows-only at runtime**. The compiled `.exe` calls `msvbvm60.dll` (Microsoft Visual Basic Virtual Machine 6.0), `oleaut32.dll` (OLE Automation), and a constellation of OCX controls (mscomctl, mscomct2, comdlg32, etc.). There is no Linux-native VB6 runtime. We have three plausible execution surfaces:

1. **Windows Server Core 2022 container** running a real VB6 runtime — production-correct, but Windows containers require Windows hosts (or hyper-v isolation), and the runtime DLLs have licence constraints.
2. **Wine on Linux** — Wine's compatibility tier rates VB6 as "Platinum" for the IDE itself and "Gold" for most compiled apps. Reliable for non-COM logic, hostile to COM-heavy code.
3. **Skip Gate 3 entirely** — degrade to a 3-gate validation for VB6. Acceptable for a Phase 10.0 MVP, unacceptable for production.

The existing equivalence sidecars (`fpc-sidecar` for Delphi, `gpp-sidecar` for C++) all use Linux containers with permissively-licensed open-source runtimes. VB6 breaks that pattern.

---

## Decision

**Two-tier strategy: Windows Server Core 2022 container for production + Wine fallback for dev environments.** Customers provide their own VB6 runtime DLLs at build time; we never redistribute them.

### Production: `vb6-sidecar` — Windows Server Core 2022 container

- Base image: `mcr.microsoft.com/windows/servercore:ltsc2022`
- Customer drops VB6 runtime DLLs into a known volume (`/runtime`) at build time via a documented setup script
- Sidecar exposes HTTP `/run` endpoint mirroring the existing `fpc-sidecar` contract
- Compile path: `vb6.exe /make <project.vbp> /out <project.exe>` (vb6.exe required; see below)
- Run path: invokes the compiled binary with the input record streamed to stdin, captures stdout
- Cross-runtime byte-compare against the .NET 10 candidate happens in the validator, not the sidecar

### Dev: `vb6-sidecar-wine` — Wine on Debian

- Base image: `debian:bookworm-slim` + Wine 9.x
- Same `/runtime` volume convention, same `/run` endpoint
- Compatible with non-COM VB6 routines; fails gracefully (clear error message) when a routine touches COM
- Lets the Linux-first dev team work on the bulk of 10.0.a / 10.0.b / 10.0.c / 10.0.d without standing up a Windows host

The validator selects between the two based on a config flag (`Validation__Vb6Endpoint` env var). Production environments point at the Windows container; dev environments default to Wine.

---

## Alternatives considered

### A. Windows Server Core 2022 container + Wine dev fallback **(chosen)**

- **Pros:** Production-correct on Windows. Dev team unblocked via Wine for most workflows. Customer holds the licence dependency; we don't redistribute restricted bits.
- **Cons:** Two container variants to maintain. Windows containers require Windows hosts or Hyper-V isolation. Customer setup step is non-trivial (acquire and place VB6 runtime DLLs).
- **Effort:** ~3 weeks for Windows variant + ~1 week for Wine variant. ~4 weeks total.

### B. Wine-only for production

- **Pros:** Linux-only stack; reuses our existing container/observability pipeline; no licensing dance.
- **Cons:** Wine's VB6 COM emulation is incomplete. Real customer codebases will exercise COM in ways Wine doesn't handle. We'd be shipping a Gate 3 that gives false greens — worse than not shipping Gate 3.
- **Effort:** ~2 weeks. **Rejected for production** but kept as the dev tier.

### C. Skip Gate 3 entirely; rely on Gates 1, 2, and 4

- **Pros:** Zero VB6-runtime engineering work. Phase 10.0 ships faster.
- **Cons:** Loses the cross-runtime parity signal that's the strongest validation in the harness. Customer programmes for high-stakes VB6 codebases (financial, medical, manufacturing) would not accept a 3-gate validation for cutover.
- **Effort:** None. **Accepted as the MVP path** (skip 10.0.f) but **rejected** as the long-term answer.

### D. Run VB6 on a managed Azure / EC2 Windows VM

- **Pros:** No container-on-Windows complexity. Native VB6 runtime support. Existing Azure tooling.
- **Cons:** Breaks the Docker-compose-everywhere pattern. Higher per-call latency (network round-trip to a VM). Cost scales with QPS. Customer-licensing dance is the same as the container path.
- **Effort:** ~3 weeks + ongoing infrastructure cost. **Rejected** — doesn't fit the architecture.

### E. Distribute the VB6 runtime DLLs ourselves

- **Pros:** Customer onboarding is one-click. No setup script.
- **Cons:** Microsoft's runtime DLLs ship as part of the Visual Studio 6 install media, licenced under the VS6 EULA. The EULA's redistribution clause is restrictive — runtime redistribution is allowed only for compiled VB6 applications under Microsoft's "Royalty-Free Redistributable Files" list, NOT for tooling that compiles VB6 code. Shipping `vb6.exe` itself is a clear violation. Shipping `msvbvm60.dll` alone is grey but exposes us to legal risk.
- **Effort:** None engineering; meaningful legal review. **Rejected** — risk-reward doesn't pencil out.

---

## Consequences

### Positive

- Production Gate 3 is faithful to real VB6 runtime behaviour. Customers get the same correctness guarantee they get from Delphi (fpc) and C++ (gcc/clang).
- Dev team isn't blocked waiting on Windows infrastructure for the 90% of work that doesn't need a real VB6 runtime.
- Licensing surface stays clean — customer owns the runtime dependency.

### Negative

- Customer onboarding includes a "place these DLLs here" step. We document it clearly, but it's friction.
- Windows containers require Windows hosts or Hyper-V isolation; many Kubernetes clusters need explicit node-pool configuration. DevOps work is more involved than for the Linux sidecars.
- Wine fallback's COM gaps mean dev-tier Gate 3 results aren't fully trustworthy for COM-heavy routines. Dev environments need a clear "production-confirmed" badge for any spec that's been validated against Wine only.

### Neutral

- The shape of the validator and the harness driver contract (ADR-032) doesn't change. `vb6-sidecar` plugs in at the same seam as `fpc-sidecar`.

---

## Operational notes

### Production container

- **Image:** `astra/vb6-sidecar:0.1.0` based on `mcr.microsoft.com/windows/servercore:ltsc2022`.
- **Runtime DLL volume:** `/runtime` (mapped to a Docker volume). Customer's setup script (shipped with the sidecar) copies DLLs from their VS6 install or MSDN-archived media into the volume.
- **vb6.exe acquisition:** customers who own a VB6 / Visual Studio 6 / MSDN Universal licence have legal rights to `vb6.exe`. Setup script verifies presence; refuses to start if missing. We document the legitimate acquisition paths in the README.
- **HTTP contract:** `POST /run` with body `{ "projectVbp": "...", "stdin": "..." }` → `{ "stdout": "...", "exitCode": 0, "elapsedMs": 42 }`. Mirrors `fpc-sidecar`.
- **Kubernetes:** dedicated `windows` node pool with Hyper-V isolation. AKS / EKS / GKE all support this; setup is documented.

### Dev / Wine container

- **Image:** `astra/vb6-sidecar-wine:0.1.0` based on `debian:bookworm-slim` + `wine9-stable`.
- **Same `/runtime` volume convention.** Wine needs `msvbvm60.dll` registered via `wine regsvr32 msvbvm60.dll`; setup script handles this.
- **COM gap behaviour:** any routine that touches COM returns a structured error (`{"error": "vb6.com.unsupported", "message": "..."}`) instead of a runtime crash. The validator marks Gate 3 as "skipped (dev-tier)" rather than "failed".
- **Latency:** Wine cold start ~600 ms; warm ~150 ms. Live with it — dev tier, not production.

### Validator integration

- New env var: `Validation__Vb6Endpoint` (production: Windows sidecar URL; dev: Wine sidecar URL).
- New `IHarnessDriver` implementation: `Vb6HarnessDriver` — extends the contract from ADR-032 with the VB6-specific compile step.
- Existing `LiveMode4thGate` flag (Phase 9.5) extends to VB6 once the equivalence story is green.

---

## Open questions

- **OQ-037-1:** Hyper-V isolation overhead on the production node pool — what's the realistic QPS per node? Spike during 10.0.f kickoff. If too low, document a sidecar-per-corpus rather than a sidecar-per-cluster pattern.
- **OQ-037-2:** Wine's COM gap detection — can we statically detect COM use from the AST and pre-mark the routine as "Wine-incompatible" before invoking the sidecar? Saves a round-trip on dev. Likely yes (any `CreateObject` / `GetObject` / `Tools → References` use); confirm during 10.0.a parser work.
- **OQ-037-3:** Customer licensing audit — does Nous need a contractual sign-off from the customer attesting their VB6 / VS6 licence covers the runtime DLLs they drop in our `/runtime` volume? Legal review before first engagement.
