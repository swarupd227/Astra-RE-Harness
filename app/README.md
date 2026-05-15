# Astra RE Harness — local Docker stack (Phase A)

This is the **Phase A · Foundations** delivery from the master plan. Everything runs in Docker; you need nothing on the host except Docker Desktop.

> **Auth is deferred.** The API uses an `X-Dev-Persona` header instead of OIDC. The frontend's top-right persona switcher writes to `localStorage` and fetches set the header automatically. Real Entra ID OIDC arrives in Phase C.

---

## What's in the stack

| Service | Port (host) | What it is |
|---|---|---|
| `frontend` | http://localhost:35173 | React 18 + TypeScript + Vite + Tailwind, HMR enabled |
| `api` | http://localhost:38080 | .NET 8 minimal API (EF Core, MediatR, Serilog, OpenTelemetry) |
| `worker` | http://localhost:38081 | Hangfire worker on Postgres; dashboard at `/hangfire` |
| `parser-sidecar` | tcp 50051 | Python + gRPC stub (fparser2 wires up in Phase C) |
| `postgres` | tcp 38432 | Postgres 16 |
| `minio` | http://localhost:39001 (console) / 39000 (S3) | S3-compatible blob store |

> **Port namespace.** Astra binds to `3xxxx` on the host so it can coexist with other local stacks (`atlas-*` on `28xxx`, `nhep-*` on `18xxx`/`19xxx`, `cude-*` on defaults). All container-internal ports are unchanged; only the host-side bindings move. Override any port in `.env`.

Buckets pre-created on first start: `sources`, `signed-specs`, `scaffolds`, `llm-debug-restricted`.

---

## Quick start

```bash
cd "C:/Astra RE Harness/app"
cp .env.example .env          # tweak anything you like
docker compose build
docker compose up -d
```

First run pulls images and builds containers; expect 3–5 minutes. Subsequent starts are seconds.

Open:

- **Frontend** → http://localhost:35173 — the Health dashboard polls the API every 5 seconds and shows every dependency green.
- **API** → http://localhost:38080/health/ready — JSON readiness probe.
- **Hangfire dashboard** → http://localhost:38081/hangfire — the `ping` recurring job fires every minute.
- **MinIO console** → http://localhost:39001 — login `astra` / `astra_dev_pw`.

To switch personas, click the persona menu in the top-right of the frontend. Selecting `sme` makes the next API call carry `X-Dev-Persona: sme`.

---

## Common operations

```bash
docker compose ps                     # see service health
docker compose logs -f api worker     # follow logs (multi-service)
docker compose restart api            # bounce the API
docker compose down                   # stop everything (volumes preserved)
docker compose down -v                # nuke volumes (DB + MinIO data)
```

To enable the optional observability stack (OpenTelemetry collector + Jaeger UI):

```bash
docker compose --profile observability up -d
# Jaeger UI: http://localhost:16686
```

---

## Editing code while the stack is running

- **Frontend.** `frontend/src` is bind-mounted into the container; Vite HMR picks up changes instantly.
- **API.** `api/src` is bind-mounted; the dev container runs `dotnet watch run` and rebuilds automatically. First rebuild after a change takes ~5–10 seconds.
- **Worker.** Same `dotnet watch run` pattern as the API.
- **Parser sidecar.** Restart the container after editing — `docker compose restart parser-sidecar`. (Will be improved when fparser2 lands.)

---

## What got built (Phase A acceptance)

The **M1 hard gate** from `04_Delivery/phase-plan-and-gates.md`, adapted to local Docker:

- [x] AKS DEV cluster reachable → **`docker compose up -d` brings the stack up**
- [x] `/health` returns 200 with DB / Blob / Parser dependencies → **`/health/ready` reports each**
- [x] Engineer persona can sign in → **persona switcher (auth deferred)**
- [x] OpenTelemetry trace from a `/health` call → **traces emit; Jaeger via the `observability` profile**
- [x] Test HSM key in DEV → **deferred to Phase B (will use a software signer behind `IHsmSigner`)**
- [x] Postgres migration applied → **`EnsureCreated` builds the schema; switches to migrations in Phase B**
- [x] Design tokens committed → **`tailwind.config.js` + `tokens/tokens.ts`**
- [x] Storybook deployed → **deferred to Phase A2 (wired in once we have ≥10 primitives)**
- [x] CI pipeline → **stub Compose-based check; GitHub Actions added when the repo is initialized**

---

## What is *not* in this phase (and where it lands)

| Capability | Phase | Why deferred |
|---|---|---|
| Microsoft Entra ID OIDC + magic-link SME flow | C | The user explicitly deferred auth. Phase A ships a dev-persona shim. |
| Stage 1 ingest UI + Octokit | C | Per the plan: the demo slice (Phase B) is built on a seeded corpus first. |
| Stage 2 parse UI + fparser2 wiring | C | Sidecar is a stub now; Phase C swaps in fparser2. |
| Stages 3–5 (extract / review / sign / scaffold) | B | Phase B builds the demo slice. |
| Azure Key Vault Managed HSM | B / D | Local dev uses a software signer behind the same interface; staging uses a real HSM in Phase D. |
| EF Core migrations (proper) | B | Phase A uses `EnsureCreated`; Phase B switches to migration files when the data model lands. |
| GitHub Actions / CI | A2 | Once the repo has a remote. The local Compose stack is the "CI" today. |

---

## Troubleshooting

**`api` keeps restarting.** Check `docker compose logs api`. Most common causes:

- Postgres not yet healthy — first start can take 20–30 seconds. Compose waits via `depends_on.condition: service_healthy`, so this should resolve itself.
- `dotnet restore` hit a network problem — `docker compose build api --no-cache` re-runs.

**Frontend can't reach API.** Check `VITE_API_BASE_URL` in `.env` matches the API published port (default `http://localhost:8080`). The browser hits the host network, not the Docker network.

**MinIO bootstrap container exited with error.** That container is one-shot; an exit code of 0 with logs `bucket '...' created` is success. To re-run: `docker compose up minio-bootstrap`.

**Hangfire dashboard says "no servers."** Wait 30 seconds after `docker compose up` and refresh — the worker takes a moment to register.

**Port collision.** Edit `.env` (e.g. `API_PORT=38090`) and `docker compose up -d`. The `3xxxx` defaults are picked to coexist with `atlas-*`, `nhep-*`, and `cude-*` stacks on the same host — but if you have something else on `3xxxx`, override there.

**API logs show `dotnet watch ❌ Exited with error code 1` after a csproj or package change.** The container's `obj/` and `bin/` are anonymous volumes; package changes need a fresh restore. Recreate with: `docker compose rm -fsv api && docker volume rm astra-re-harness_api-obj astra-re-harness_api-bin && docker compose up -d api`. Same pattern for the worker.

**API logs show `An item with the same key has already been added. Key: /src/.../obj/...`** That's `dotnet watch`'s polling watcher tripping on the Windows bind mount. Already mitigated in `docker-compose.yml` with anonymous volumes for `obj/` and `bin/`. If you ever remove those mounts, the watcher will start crashing again.

---

## Project layout

```
app/
├── docker-compose.yml
├── .env.example
├── README.md                  ← you are here
├── infra/
│   ├── postgres/init.sql      ← extensions on first DB start
│   ├── minio/bootstrap.sh     ← bucket creation
│   └── otel/collector-config.yaml
├── api/                       ← .NET 8 minimal API
│   ├── Dockerfile
│   └── src/Astra.Api/
├── worker/                    ← .NET 8 Hangfire worker
│   ├── Dockerfile
│   └── src/Astra.Worker/
├── parser-sidecar/            ← Python + gRPC stub
│   ├── Dockerfile
│   └── parser_sidecar/
└── frontend/                  ← React + Vite
    ├── Dockerfile
    └── src/
```

Plan and supporting design artefacts live one directory up at `C:/Astra RE Harness/`.
