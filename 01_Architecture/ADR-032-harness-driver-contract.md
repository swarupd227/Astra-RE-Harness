# ADR-032 — HarnessDriver contract + per-run binary cache

**Status:** Draft (proposed)
**Phase:** 9.5 (kickoff)
**Companion:** `phase-9.5-4th-gate-v1.1.md`, ADR-029, ADR-030

---

## Context

Phase 9.5 swaps the 4th gate's shadow-mode callback for real
reference-vs-candidate binary comparison. The
`property-test-sidecar` drives Hypothesis end-to-end and posts ~100
generated inputs per claim to `/internal/equivalence-callback`; the
callback must:

1. Look up the cached reference binary for this validation run.
2. Look up the cached candidate binary for this validation run.
3. Feed the input to both, capture stdout, compare.
4. Return `{agree, refOutput, candOutput}`.

Two design questions decide the shape of the work:

- **How are the binaries cached?** Per-callback compilation is
  hopeless (each compile is 30–60s; 100 callbacks would be 50+
  minutes). Per-validation-run caching with a in-process map is the
  only path that fits the 10-min/spec budget.
- **How do the binaries accept one input at a time?** Spec inputs
  are typed (int, float, string, …). The driver needs to convert one
  JSON line on stdin into the routine's argument list, call the
  routine, and emit one JSON line on stdout.

Both decisions are settled here so the validator code is
mechanically straightforward.

---

## Decision

**Two parts:**

### 1. `PropertyTestRunCache` — in-process singleton

A `ConcurrentDictionary<Guid, BinaryArtifacts>` keyed by the
validation run id. `BinaryArtifacts` carries:

```csharp
public sealed record BinaryArtifacts(
    string  RefSidecar,         // "gfortran" | "fpc" | "gpp" | "gnucobol"
    string  RefArtifactId,      // sidecar-side handle for /run
    string  CandidateExePath,   // absolute filesystem path
    string  CandidateRunner,    // "dotnet" | "java"
    string  CandidateTempDir,   // cleanup target on eviction
    IReadOnlyList<string> InputNames  // ordered list matching spec.inputs[*].name
);
```

The validator populates this row before calling /falsify and removes
it after /falsify returns (success or error). Subsequent callbacks
look up the row by `runId` query string, run both binaries, return
the diff.

### 2. `HarnessDriver` — per-archetype entry point contract

Every scaffold archetype that wants to participate in live-mode 4th-
gate execution ships a **`HarnessDriver` entry point** that:

- Reads exactly one line from stdin
- Parses that line as a JSON object `{<inputName>: <value>, ...}`
  where the keys MATCH the spec's `inputs[*].name`
- Calls the routine with the parsed arguments
- Prints exactly one JSON object to stdout summarising the routine's
  output (a `{<outputName>: <value>, ...}` shape)
- Exits with status 0 on success, non-zero on any thrown exception

The reference binary's source (compiled by the appropriate sidecar)
ships an analogous driver — for Fortran it's a small `driver.f` that
reads stdin via `READ(*, …)`, calls the routine, and `WRITE`s the
result. The Fortran driver lives next to the spec's seed corpus
because the sidecar compiles it from scratch on every cache miss.

---

## Alternatives considered

### A. Per-callback compile

- **Pros:** No cache; no eviction races.
- **Cons:** 100 compiles per claim × 30s/compile = 50+ minutes per
  claim. Fails the 30s/claim and 10min/spec budgets by an order of
  magnitude.
- **Rejected.**

### B. Long-lived stdin/stdout coprocess

- **Pros:** Sub-50ms per callback after warmup. Best possible
  throughput.
- **Cons:** Subprocess pipe management is brittle (stuck reads,
  EOF detection, signal handling). v1.1 is the wrong scope to land
  this.
- **Deferred to v1.2.** v1.1 ships spawn-per-input; v1.2 swaps in
  coprocess pipes behind the same callback surface.

### C. In-memory candidate via Roslyn `CSharpScript`

- **Pros:** No filesystem path; no spawn cost; in-process.
- **Cons:** Only works for the dotnet8 target; Java + native
  targets still need a subprocess. Inconsistent behaviour across
  language pairs is confusing to debug.
- **Rejected** for cross-target consistency.

### D. PropertyTestRunCache + HarnessDriver **(chosen)**

- **Pros:** Single architectural shape across all language pairs.
  Per-run cache is straightforward to reason about. HarnessDriver
  contract is a small additive surface on each archetype —
  archetypes that haven't opted in stay in shadow mode without
  blocking the harness.
- **Cons:** Spawn-per-input is ~200ms/callback; not the fastest
  possible. Acceptable for v1.1.
- **Effort:** ~3 days for the cache + ref binary path + canonical-
  minpack HarnessDriver + tests.

---

## Consequences

### Positive

- **Per-run cost amortizes correctly.** Two compiles per
  validation run + 100 fast subprocess spawns. Comfortable in the
  10-min/spec budget.
- **Additive opt-in.** Archetypes without HarnessDriver stay in
  shadow mode. Phase 9.5 doesn't break the existing 8 archetype
  paths; only canonical-minpack gets the upgrade for v1.1.
- **One callback contract for all language pairs.** When the Delphi
  / C++ HarnessDrivers land in v1.2, the validator code is
  unchanged.

### Negative

- **HarnessDriver source duplicates the routine signature.** Each
  archetype's driver needs to know the spec's input names + types.
  Mitigation: the driver source is generated from the signed spec
  at scaffold-generation time, not hand-written.
- **Cache lifecycle is now part of the validator's correctness
  surface.** Eviction-after-return must be defensive; an orphan
  tempdir is a disk leak. Add a process-shutdown sweep that cleans
  the tempdir parent.
- **Per-run cache lives in process memory.** A pod restart mid-run
  loses the cache, and the /falsify callback fails. Mitigation:
  the property-test sidecar treats non-200 callbacks as
  `agree:true` (already established in ADR-029), so a pod restart
  degrades gracefully into shadow-mode-equivalent behaviour for the
  remainder of that run.

### Neutral

- Cache hit/miss metrics surface on the validation run's
  `MetricsJson` for SOC2/HIPAA visibility into 4th-gate latency.
- The HarnessDriver's stdin/stdout discipline is the same whether
  the candidate is dotnet, mvn, or native — the runner pivots
  to the right command but the pipe shape is identical.

---

## Operational notes

- **Cache mount:** `/var/tmp/astra-4th-gate-runs/<runId>/` holds
  candidate tempdirs. A startup-time sweep removes stale dirs.
- **Process shutdown hook:** the API's `IHostApplicationLifetime.ApplicationStopping`
  callback evicts every row in `PropertyTestRunCache` so SIGTERM
  during a 4th-gate run doesn't leak.
- **Concurrency:** the cache uses a `ConcurrentDictionary` so
  callbacks for different runs can fire in parallel. Per-run
  callbacks serialise via the run's own `SemaphoreSlim` (so the
  ref+cand spawn pair for one input doesn't race the next input).

---

## Open questions

- **OQ-032-1:** Where does the Fortran HarnessDriver source live?
  Two choices: ship it in the archetype manifest (engineer reads
  it as scaffold), or generate it at validator time from the
  signed spec's `inputs`. Generation wins for staying-in-sync;
  manifest wins for visibility. Lean toward generation in v1.1,
  manifest in v1.2.
- **OQ-032-2:** Should the HarnessDriver's JSON output schema be
  validated against the spec's `outputs`? Useful as a tripwire,
  but the comparison itself already catches divergence. Defer.
- **OQ-032-3:** What's the cleanup contract for the candidate
  tempdir when the validator crashes mid-run? Current plan: the
  startup sweep handles it. Belt-and-braces: add a 24-hour TTL
  on each tempdir's parent directory.
