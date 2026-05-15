# CI/CD & Environments

**Status:** Kickoff (v0.1)
**Companion:** `phase-plan-and-gates.md`, `01_Architecture/architecture-overview.md`

---

## 1. Environments

| Environment | Provisioned | Purpose | Identity | Source data | LLM endpoints | HSM key |
|---|---|---|---|---|---|---|
| **DEV** | Week 1 | Engineer dev loop, integration tests | Entra ID (DEV tenant or DEV app reg) | Synthetic + opt-in real Kiwiplan corpus | Real ZDR endpoints (low quota) | Test HSM key |
| **STAGING** | Week 5 | Demo rehearsals, UAT, perf testing | Entra ID (PROD tenant or STAGING app reg) | Real Kiwiplan source (read-only) | Real ZDR endpoints | Staging HSM key |
| **PROD** | Week 14 | Phase 1 production use | Entra ID (PROD tenant) | Real Kiwiplan source | Real ZDR endpoints | PROD HSM key |

Single Azure subscription, three resource groups (`rg-astra-dev`, `rg-astra-staging`, `rg-astra-prod`). All region-pinned to one Azure region (decision pending; see master plan §12 question 3).

---

## 2. Branching & promotion

```
feature/* ──PR──▶ main ──merge──▶ DEV (auto, every merge)
                              ──nightly──▶ STAGING (or on-demand for rehearsals)
                              ──manual──▶ PROD (Helm deploy + release note)
```

- **`main` is the trunk.** Trunk-based development with short-lived feature branches.
- **Branch protection on `main`:** required PR review (≥1 reviewer), required CI green.
- **No release branches.** Releases are tags pointing at `main` commits.
- **Build-once-promote-many:** the same container image is promoted across DEV → STAGING → PROD. Helm value overrides per environment.

---

## 3. Pipeline structure

GitHub Actions, with reusable workflows for each container.

### 3.1 PR pipeline (every push to a feature branch)

1. **Lint** — TypeScript ESLint, Prettier, .NET `dotnet format --verify-no-changes`, Python `ruff` (parser sidecar).
2. **Build** — TypeScript compile, .NET build, Python deps cached.
3. **Unit tests** — `dotnet test`, `vitest run`, `pytest`.
4. **Integration tests** — Spin up Postgres + Hangfire in-process; run `*.IntegrationTests` projects against test containers.
5. **OpenAPI consistency** — Regenerate frontend types from OpenAPI; fail if diff.
6. **Security scans** — `dotnet list package --vulnerable`, `npm audit --omit=dev`, container scan via Trivy.
7. **Coverage report** — Posted as PR comment.

### 3.2 Merge-to-main pipeline (auto-deploys to DEV)

In addition to the PR pipeline:

1. **Container build** — Multi-arch images for api, worker, frontend, parser-sidecar; push to ACR with `:sha-<short>` and `:dev-latest` tags.
2. **Helm deploy to DEV** — `helm upgrade --install astra ./charts/astra --values values.dev.yaml --set image.tag=sha-<short>`.
3. **Migration hook** — Helm pre-install/upgrade hook runs `dotnet ef database update`. Failure stops the deploy and rolls back to the previous image (no migration is reversed).
4. **Smoke tests** — `/health` returns 200; auth flow can sign in; one E2E test runs.
5. **Notification** — Deploy summary posted to internal channel.

### 3.3 Nightly STAGING pipeline

1. **Promote** the last green DEV image to STAGING (`:staging-latest`).
2. **Helm deploy to STAGING** with `values.staging.yaml`.
3. **Migration hook** runs.
4. **Full E2E suite** — Playwright runs the demo flow (Stage 3 → 4 → 5).
5. **Performance smoke** — A small load test against §9.1 budgets; alert on regression >20%.

### 3.4 PROD release (manual)

A human triggers `release.yml` with a target SHA:

1. Verify the SHA has been on STAGING for at least 24 hours and the nightly E2E was green.
2. Tag the commit `release/vYYYY-MM-DD-NN`.
3. Promote the image to `:prod-<NN>`.
4. **Source-residency CI gate:** before deploy, run `verify-config-version.sh prod` against the manifest in `deploy/manifests/prod.yaml`. Fail the release on mismatch.
5. **Helm deploy to PROD** with `values.prod.yaml`.
6. **Migration hook** runs.
7. **Smoke tests** + **post-release verification** run automatically.
8. **Release note** auto-drafted from the diff since the previous PROD release; reviewed and published manually.

---

## 4. Migrations

EF Core migrations are part of the deployment artifact. Forward-only.

- **Local dev:** developers run `dotnet ef database update` against their local Postgres.
- **DEV / STAGING / PROD:** Helm pre-install/upgrade hook runs migrations before pods start.
- **Failure handling:** a failed migration halts the deploy; the chart rolls back to the previous image. Migrations are *not* reversed automatically — the engineer fixes forward.
- **Review:** every migration PR has ≥2 reviewers (BE + Platform).
- **Squash policy:** migrations are squashed at major milestones (M2, M3) for cleanliness, never mid-phase.

---

## 5. Secrets

| Secret | Source | Consumer |
|---|---|---|
| Postgres connection | Key Vault, surfaced via managed identity | API + Worker |
| Anthropic API key | Key Vault | Worker |
| Azure OpenAI key | Key Vault | Worker |
| Octokit service-account PAT | Key Vault | Worker |
| OpenTelemetry collector | Key Vault | API + Worker |
| HSM key reference | Key Vault key URI (not a secret per se) | API |

Secrets never appear in environment variables, container images, or CI logs. The pipelines fetch secrets at deploy time using federated workload identity.

---

## 6. Observability of the pipeline itself

- **Build telemetry.** Every CI run sends duration + outcome to Azure Monitor.
- **Deploy telemetry.** Helm install/upgrade events recorded as deployment markers in dashboards. Latency and error-rate panels show deploy markers as vertical lines.
- **Post-deploy alert window.** A 30-minute heightened-alert window after every PROD deploy. If error rate or p99 latency regresses, automatic rollback fires.

---

## 7. Rollback

- **Helm rollback** is the standard mechanism: `helm rollback astra <revision>`.
- **Database rollback** is by forward-fix migration only. We do not run `Down` migrations.
- **Post-rollback verification.** The rollback runbook ends with the same smoke tests that gate forward deploys.
- **Communication.** Any rollback in PROD triggers an incident-channel message and a one-paragraph post-rollback note in the release log.

---

## 8. Environment parity & drift detection

- **IaC.** All three environments are described in Bicep/Terraform. Manual changes to PROD are forbidden; if made under emergency, a follow-up PR backfills the change into IaC within 24 hours.
- **Config drift detection.** A nightly job runs `terraform plan` against PROD and reports any drift to the platform channel.
- **Provider config drift.** The source-residency CI gate (`risk-register.md` R-06) is the runtime drift detector for LLM provider configuration.

---

## 9. Local development loop

- **Backend:** `dotnet run --project src/api` against a local Postgres (Docker Compose). Hangfire runs in-process for development.
- **Frontend:** `npm run dev` (Vite). Mocked API via MSW (Mock Service Worker) for component-level work; against the real DEV API for integration.
- **Parser sidecar:** `docker compose up parser-sidecar` runs the Python service locally.
- **Auth:** local dev users are issued short-lived JWTs from a stub OIDC provider; persona is settable via a query param in dev only.
- **HSM:** local dev uses a software-only signer behind the same `IHsmSigner` interface; a CI gate prevents the software-only implementation from compiling into staging or PROD images.
