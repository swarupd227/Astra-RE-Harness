# Phase 5 · Foundation checkpoint (5.0 → 5.4)

Tracking artefact for the COBOL → Java Spring migration foundation work.
The five commits referenced below are already on `main`; this checkpoint
exists so a reviewer has a stable PR thread to discuss them against.

For the **actual file diff** across the five commits, use the GitHub
compare URL:

→ https://github.com/swarupd227/Astra-RE-Harness/compare/b36abd3...phase-5/foundation-5.0-to-5.4

## Commits in this slice

| # | Commit | Title |
|---|---|---|
| 5.0 | `e7ae8b2` | COBOL → Java migration intake document |
| 5.1 | `af095f5` | COBOL-85 parser in the parser sidecar |
| 5.2 | `2be0c4f` | Pipeline routing by source language |
| 5.3 | `2b5bf56` | `cobol/java-spring/extract` prompt (production) |
| 5.4 | `479ba13` | COBOL → Java Spring scaffold archetype |

## End-to-end path enabled after this slice

```
DEPTPAY.CBL upload
  → parser-sidecar (COBOL branch)                 [5.1]
  → Subroutine row, sourceLanguage="cobol"        [5.2]
  → ExtractionPipeline picks
     cobol/java-spring/extract@v0.1                [5.3]
  → AnthropicLlmProvider sends COBOL prompt
  → Spec claims persisted
  → SME signs
  → POST /scaffold?targetStack=java-spring
  → Gate evaluates cobol-canonical-payroll
     (production, matches DEPTPAY)                 [5.4]
  → MockScaffoldProvider streams the 7-file
     Maven module                                  [5.4]
```

## Catalog state after this slice

`GET /api/v1/archetypes` returns four archetypes:

- `dotnet8/canonical-rollstock` · production · fortran-f77
- `dotnet8/cobol-canonical-rollstock` · production · cobol
- `java-spring/canonical-rollstock` · preview · fortran-f77 + cobol
- `java-spring/cobol-canonical-payroll` · **production** · cobol *(new in 5.4)*

## Test coverage added

17 new Playwright e2e tests + 6 Python unit tests for the parser:

| Phase | Test file | Tests |
|---|---|---|
| 5.1 | `app/parser-sidecar/tests/test_cobol_parser.py` | 6 unit (DEPTPAY/EMPPAY shapes, PERFORM-UNTIL guard, COPY books, comment indicators, missing PROGRAM-ID) |
| 5.2 | `app/e2e/tests/language-routing.spec.ts` | 4 e2e (COBOL routing, Fortran regression, back-compat, unsupported-extension reject) |
| 5.3 | `app/e2e/tests/cobol-java-extract-prompt.spec.ts` | 3 e2e (catalog presence, calibration markers, frontmatter scalars) |
| 5.4 | `app/e2e/tests/cobol-java-archetype.spec.ts` | 4 e2e (registration, file manifest, anyOf matches, gate-fix proof) |

## Remaining Phase 5 work (5.5 → 5.7)

- **5.5** — `maven-sidecar` container + Java Compile/TestPack validators
- **5.6** — `gnucobol-sidecar` container + per-routine COBOL ↔ Java equivalence harness
- **5.7** — End-to-end captioned demo recording
