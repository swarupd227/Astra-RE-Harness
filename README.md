# Nous Astra RE Harness — Development Artefacts

This folder is the development plan and supporting artefacts for **Nous Astra RE Harness** (the AI-assisted Fortran→.NET reverse-engineering tool specified in `Nous_RE_Harness_Product_Specification_v1.docx`).

**Plan version:** v0.1 (kickoff draft)
**Plan date:** 2026-05-05
**Plan owner:** Engineering lead (TBA)
**Source spec location:** `C:\Users\swarupd\Downloads\Nous_RE_Harness_Product_Specification_v1.docx`

---

## How to read these documents

Start here:

1. **[00_DevelopmentPlan.md](00_DevelopmentPlan.md)** — the master plan. Phasing, gates, team, UX strategy in summary, decision log, open questions. Read this first.

Then go deep on whichever lens matches your role:

| If you are… | Read… |
|---|---|
| Engineering lead | All five folders below, in order. |
| Backend engineer | `01_Architecture/` then `03_Backlog/prioritized-stories.md` |
| Frontend engineer | `02_UX/` then `03_Backlog/prioritized-stories.md` |
| Product designer | `02_UX/` (all four documents) |
| Platform / DevOps | `01_Architecture/architecture-overview.md`, `01_Architecture/security-and-residency.md`, `04_Delivery/ci-cd-and-environments.md` |
| Security / compliance | `01_Architecture/security-and-residency.md`, `04_Delivery/risk-register.md` |
| Project / programme | `00_DevelopmentPlan.md`, `03_Backlog/epics-and-milestones.md`, `04_Delivery/phase-plan-and-gates.md` |

---

## Folder map

```
C:\Astra RE Harness\
├── README.md                                      ← you are here
├── 00_DevelopmentPlan.md                          ← master plan
│
├── 01_Architecture\
│   ├── architecture-overview.md                    System, data flow, state machine
│   ├── data-model.md                               Entities, schemas, signing
│   ├── api-surface.md                              Endpoints, SSE protocol, errors
│   ├── llm-integration.md                          Providers, prompts, observability
│   └── security-and-residency.md                   Controls, audit, compliance
│
├── 02_UX\
│   ├── ux-vision-and-principles.md                 Trace · Pace · Trust
│   ├── design-system.md                            Tokens, primitives, motion
│   ├── information-architecture.md                 Navigation, URLs, search
│   └── screen-blueprints.md                        Per-screen interaction spec
│
├── 03_Backlog\
│   ├── epics-and-milestones.md                     Epic ↔ milestone matrix
│   ├── definition-of-done.md
│   └── prioritized-stories.md                      Phase A + B working backlog
│
├── 04_Delivery\
│   ├── phase-plan-and-gates.md                     Week-by-week, gate criteria
│   ├── risk-register.md
│   ├── ci-cd-and-environments.md
│   └── demo-build-plan.md                          Defense-call demo, expanded
│
└── 05_Reference\
    ├── spec-pointer.md                             Map from spec sections to plan artefacts
    └── source-spec-extract.md                      Plain-text extract of the source spec
```

---

## The two delivery commitments

| Commitment | Date target | Document |
|---|---|---|
| **Defense-call demo** (4-minute Appendix A flow on STAGING) | End of week 6 (≈ 2026-06-15) | [`04_Delivery/demo-build-plan.md`](04_Delivery/demo-build-plan.md) |
| **Phase 1 production cutover** (≥10 real subroutines through full cycle) | End of week 16 (≈ 2026-08-28) | [`04_Delivery/phase-plan-and-gates.md`](04_Delivery/phase-plan-and-gates.md) §Phase E |

Everything else in this folder is in service of those two commitments.

---

## Status of the artefacts

All documents in this folder are **v0.1 — kickoff drafts**. They are intended to be the seed of a living development plan: each will evolve as ADRs land, risks resolve or surface, and stakeholder questions answer.

The next planned revision is the end of Phase A (≈ 2026-05-17), at which point:

- Open questions in `00_DevelopmentPlan.md` §12 should be answered.
- The first ADRs (Hangfire choice, fparser2 sidecar, Auth.js choice) should be merged.
- Phase C backlog should be drafted.
- Token set should be locked and published in Storybook.

---

## How to make changes to these documents

- For substantive revisions, raise a PR-style change in your tracking tool referencing the affected files. Each file has an explicit `Status` line at the top — bump the version and date when you revise.
- Decisions land in `00_DevelopmentPlan.md` §11 and as ADRs under `04_Delivery/adr/` (folder to be created when the first ADR lands).
- The source `.docx` spec is canon; if a plan artefact contradicts the spec, the plan needs to update.
