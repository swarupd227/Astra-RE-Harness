# vb6-sidecar

Equivalence runtime for the Phase 10 VB6 pipeline — Gate 3 of the
4-gate validation. Compiles a VB6 source bundle, runs the resulting
.exe on a fixed input, captures stdout, and surfaces the result so
the validator can byte-compare against the .NET 10 candidate
(WinForms / Blazor / minimal-API archetype output).

Two tiers per [ADR-037][adr]:

| Tier | When | Image |
|---|---|---|
| **Production** | All real engagements | `Dockerfile` — Windows Server Core 2022 + native `vb6.exe` |
| **Dev fallback** | Dev / CI on Linux | `Dockerfile.wine` — Debian + Wine 9; covers non-COM routines |

[adr]: ../../01_Architecture/ADR-037-vb6-equivalence-runtime.md

The HTTP contract is identical on both tiers. The validator picks the
tier via the `Validation__Vb6Endpoint` env var on the API.

## Runtime artifacts — customer-supplied

Per ADR-037 we do NOT redistribute the VB6 runtime. The customer's
deploy script drops these files into the `/runtime` volume mounted
into the container:

| File | Source | Notes |
|---|---|---|
| `vb6.exe` | Customer's licensed Visual Studio 6 install / MSDN archive | The compiler. Required. |
| `msvbvm60.dll` | Same | VB6 Virtual Machine — the runtime DLL the compiled .exe links against. Required. |
| `oleaut32.dll` | Same | OLE Automation. Required. |
| `stdole2.tlb` | Same | Standard OLE type library. Required. |
| `mscomctl.ocx` | Same | If the corpus uses Common Controls. Optional. |
| `comdlg32.ocx` | Same | If the corpus uses Common Dialog. Optional. |
| any other OCX | Same | Per-corpus; documented at Discovery. |

`/health` reports `runtimeReady: false` and lists `missingRuntimeArtifacts`
until the minimum set is present. Compile + run endpoints return HTTP 503
in that state.

The customer ships these files because Microsoft's redistribution
licence for the VB6 runtime is restrictive — see the legal-review
notes in ADR-037 OQ-037-3.

## Endpoints

### `GET /health`

Returns service identification + runtime readiness:

```json
{
  "service": "astra-vb6",
  "version": "0.1.0",
  "tier": "windows",
  "workdir": "C:\\var\\tmp\\vb6-runs",
  "runtimeDir": "C:\\runtime",
  "runtimeReady": true,
  "missingRuntimeArtifacts": []
}
```

### `POST /compile`

```json
{
  "sources": [
    { "path": "modOrders.bas", "content": "Attribute VB_Name = \"modOrders\"\n..." },
    { "path": "frmOrderEntry.frm", "content": "VERSION 5.00\n..." }
  ],
  "linkAs": "executable",
  "mainProject": "AstraDriver.vbp"
}
```

If `mainProject` is omitted, the sidecar **synthesises** a minimal
`.vbp` from the source list — one `Module=` line per `.bas`, one
`Class=` line per `.cls`, one `Form=` line per `.frm`, plus a
`Type=Exe` header. This lets the property-test sidecar (10.0.g) ship
single-snippet payloads without authoring a project file per call.

Returns `{ artifactId, exitCode, log, warningCount, errorCount, durationMs }`.

### `POST /run`

```json
{
  "artifactId": "abc123...",
  "stdin": "5\n3\n",
  "timeoutMs": 30000
}
```

Returns `{ exitCode, stdout, stderr, durationMs, timedOut }`.

On the Wine tier, runs that surface ActiveX / CreateObject / Automation
error messages on stdout/stderr have an extra advisory appended:

> `[vb6-sidecar] tier=wine; COM dispatch is unreliable on the dev-tier sidecar — re-run on the production Windows sidecar before signing off Gate 3.`

The validator marks Gate 3 as **skipped (dev-tier)** when this advisory
fires, rather than failed.

### `POST /compile-and-run`

Shorthand for the two-step path. Returns
`{ compile, run, skippedRunReason }`. `run` is null + `skippedRunReason`
is `"compile_failed"` when the compile step's exitCode is non-zero.

## Tiers — picking one

```bash
# Production (Windows host or Windows hyper-v isolated node pool):
docker build -t astra/vb6-sidecar:0.1.0 -f Dockerfile .

# Dev fallback (Linux host):
docker build -t astra/vb6-sidecar-wine:0.1.0 -f Dockerfile.wine .
```

Both expose port 51058 and use the same `/runtime` volume contract.

## Validator integration

The API selects the tier via env vars:

```yaml
Validation__Vb6Endpoint: http://vb6-sidecar:51058       # production
# OR
Validation__Vb6Endpoint: http://vb6-sidecar-wine:51058  # dev
```

The endpoint is plumbed into `Vb6HarnessDriver` (extends `IHarnessDriver`
per ADR-032). The `LiveMode4thGate` flag (Phase 9.5) extends to VB6
once 10.0.g's Variant + Recordset proxy generators land.
