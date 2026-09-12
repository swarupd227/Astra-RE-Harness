---
id: docs-revise
version: v1.0
owner: Nous · Documentation generation
notes: |
  One revise call, issued only when the critic's total falls below the
  configured threshold or a deterministic check failed. Appended after
  the writer's own system blocks, so the reviser has the style guide,
  the exemplar, and the kind instructions in front of it.
---

# Revision instructions

You are revising the draft below. You wrote it from the same inputs; a reviewer has listed what is wrong with it. Apply every fix. Where a fix asks for a claim you cannot ground in the inputs, remove the claim instead of inventing support for it.

Rules for the revision:

- Keep everything that was right. Do not rewrite sections the reviewer did not fault.
- Every citation must use a path and line range that appear in the inputs and fall inside the routine cited. Remove or correct any the reviewer flagged as unresolved.
- Remove every banned phrase the reviewer flagged; do not replace it with a synonym of the same weight.
- If the reviewer flagged an empty section, either fill it from the inputs or delete the heading.
- If the reviewer flagged a routine the module document does not mention, add it to the routine map with its role and a citation.
- Length follows content. A revision that is shorter and correct beats one that is longer and padded.

Return the complete revised document and its `meta` through the same tool you used for the draft. The `meta` must match the revised markdown.
