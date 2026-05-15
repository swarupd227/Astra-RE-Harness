# Definition of Done

**Status:** Kickoff (v0.1)
**Audience:** All engineers, designer, QA, PM
**Companion:** `epics-and-milestones.md`, `prioritized-stories.md`

A story is **done** when *all* of the following are true. The default disposition is "not done" — any unticked box keeps the story in progress.

---

## 1. Functional

- [ ] Acceptance criteria written into the story have explicit pass evidence (test report, screenshot, recorded interaction).
- [ ] Loading, empty, and error states implemented and reviewed where applicable.
- [ ] Edge cases identified in the story are exercised in tests.
- [ ] Backend: input validation via FluentValidation; rejected inputs return the structured error model.
- [ ] Frontend: form fields have inline validation, disabled/invalid states, and labels.
- [ ] State transitions are enforced server-side, not only client-side.

---

## 2. Tests

- [ ] Unit tests cover the new logic paths.
- [ ] Integration tests cover the new endpoint(s) and database state transitions.
- [ ] E2E (Playwright) coverage exists for any happy-path user journey impacted by the story. **Sign-off-related stories require dedicated E2E coverage from week 5.**
- [ ] Coverage: ≥75% on backend domain code, ≥60% on frontend components affected.
- [ ] All tests run green in CI on the merge commit.

---

## 3. Telemetry

- [ ] New API endpoints emit OpenTelemetry spans with request ID, persona, target resource ID.
- [ ] LLM-touching code records `LlmCall` with all spec §6.4 attributes.
- [ ] State transitions emit an `AuditEvent` row (and where appropriate, a span event).
- [ ] Errors surface via `ILogger` with structured fields; no stack traces in production logs unless tagged.
- [ ] PII / RESTRICTED data is *not* logged (verified by the logging-middleware allowlist test).

---

## 4. Security

- [ ] New endpoints have a persona policy attached. CI fails if any endpoint is unannotated.
- [ ] Sensitive operations (sign, route, credential ops) are audited.
- [ ] Inputs that go to provider/external services are validated.
- [ ] No new secrets in code; secrets stored in Key Vault.
- [ ] Threat surface change documented in the story (one paragraph). Phase D's threat model picks these up.

---

## 5. Accessibility

- [ ] Keyboard navigable: every interactive element reachable, focus order logical.
- [ ] Focus visible (uses `border.strong` token).
- [ ] Color is not the only signal (badges include icon + text + color).
- [ ] ARIA roles/labels per `screen-blueprints.md`.
- [ ] Reduced-motion behavior verified.
- [ ] Contrast ratio ≥4.5:1 for body, ≥3:1 for large text. Verified per token in Storybook.

---

## 6. Performance

- [ ] Performance-budgeted operations (per spec §9.1) measured against budget. CI alerts if regressed.
- [ ] No N+1 query introduced (verified in EF Core query log on integration tests).
- [ ] Frontend bundle size delta reported in PR; new vendor dependencies justified.

---

## 7. Documentation

- [ ] OpenAPI spec regenerated and committed.
- [ ] User-facing copy reviewed by designer.
- [ ] Internal runbook updated if the story affects operations (e.g., new admin action).
- [ ] ADR opened for any non-trivial decision.

---

## 8. Review

- [ ] PR reviewed by ≥1 engineer in the relevant discipline (BE, FE, Platform).
- [ ] Designer reviews any UI change before merge (Loom or live walkthrough acceptable).
- [ ] PR description references the story and the milestone.
- [ ] CI green; branch up to date with `main`; no merge conflicts.

---

## 9. Definition of "released"

A story merged to `main` is **released to DEV** automatically. **STAGING release** happens nightly (or on demand for demo rehearsals). **PROD release** is a deliberate, human-triggered Helm deploy with a release note in the operational channel.

The Helm pre-install hook runs migrations. A failed migration stops the release; the release is rolled back to the previous image (no migration is reversed; forward-fix only).

---

## 10. Definition of "shippable"

For Milestone gates (M1–M5), additional criteria apply per `04_Delivery/phase-plan-and-gates.md`. A milestone is **shippable** when:

- Every story rolling up to that milestone is **done** by §1–§9.
- Milestone-specific gate criteria are green.
- Joint sign-off recorded by Eng lead, Designer, PM.
