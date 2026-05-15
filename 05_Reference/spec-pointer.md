# Source Specification Pointer

The authoritative product specification is the Word document the user provided:

**Original file:** `C:\Users\swarupd\Downloads\Nous_RE_Harness_Product_Specification_v1.docx`
**Title:** *Nous Astra RE Harness — AI-assisted reverse engineering for Fortran/Classic source*
**Subtitle:** PRODUCT SPECIFICATION · ENGINEERING REFERENCE · v1.0
**Audience marker (in-document):** "Engineering — product build team"

A plain-text extract of the document is committed alongside this file as `source-spec-extract.md`. The extract is a working aid — it preserves text, headings, and code blocks, but loses tables, figures, and page formatting. **For any disagreement between the extract and the original `.docx`, the `.docx` is canonical.**

---

## How this development plan relates to the spec

| Spec section | Plan artifact |
|---|---|
| §1 Product scope | `00_DevelopmentPlan.md` §2, §9 |
| §2 Users & personas | `02_UX/ux-vision-and-principles.md` §4 |
| §3 System architecture | `01_Architecture/architecture-overview.md` |
| §4 Screen-by-screen | `02_UX/screen-blueprints.md`, `02_UX/information-architecture.md` |
| §5 Data model | `01_Architecture/data-model.md` |
| §6 LLM integration | `01_Architecture/llm-integration.md` |
| §7 API surface | `01_Architecture/api-surface.md` |
| §8 Security & residency | `01_Architecture/security-and-residency.md` |
| §9 NFRs | `00_DevelopmentPlan.md` §4 (Phase D), `04_Delivery/phase-plan-and-gates.md` |
| §10 Project 3 integration | `01_Architecture/architecture-overview.md` §6 |
| Appendix A Demo subset | `04_Delivery/demo-build-plan.md` |
| Appendix B `CONSUME_ROLL` sample | Used as seed data; story 4-14 |

---

## Change control

The spec is treated as **canon** for v1. Any deviation from the spec in this plan is captured as a deliberate decision in `00_DevelopmentPlan.md` §11. Stakeholder-driven scope changes follow the change-control template (TBD `04_Delivery/change-control-template.md`).
