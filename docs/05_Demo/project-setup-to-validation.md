# From project setup to final validation

*The whole path for one project, in the order it is done, with who does each step, what you see, and how long it
takes. The demo-length version of steps 6–11 for a macro-enabled workbook is
`macro-workbook-migration-walkthrough.md`.*

## Roles

| Role | Does |
|---|---|
| **Admin** | Sets the model key (Platform → LLM), generates documentation and the Requirements pack, runs assessments |
| **Engineer** | Uploads, extracts specs, generates code, runs the validation gates |
| **SME** | Reviews and signs specs (nothing is generated from an unsigned spec) |
| **Observer** | Reads |

Switch role with the persona menu (top right). Everything below can also be typed as a sentence in the
conversation; the buttons run the same actions.

## The steps

| # | Step | Who | Where | What you see | Time |
|---|---|---|---|---|---|
| 1 | **Check the model key** | Admin | Platform → LLM | Provider "Claude (Anthropic)", key present. A real model is needed from step 7; without one the mock provider only proves the plumbing | seconds |
| 2 | **Create the project** | Engineer | Projects → New project | Name it, drop the files (a zip, individual source files, or Office files such as `.xlsm`/`.xls`), upload. Notes appear: "Read N VBA modules from *Book.xlsm* …", "Kept *Book.xlsm* as evidence …" | seconds to a minute |
| 3 | **Check what was read** | Engineer | Project page → *Files in this version* | Every file with its routines and line ranges; for workbooks, files are named `Book.xlsm/Module1.bas` | — |
| 4 | **Inspect the evidence** | any | Project page → *Browse evidence* (filter **Spreadsheets** for workbooks) | Sheets, named cells, input rules, buttons and the macro each runs, form controls, links between them | — |
| 5 | **Add more evidence** (optional) | Engineer / SME | Project page → *Artefact sources* | Tickets, transcripts, documents, configs; each is cut into citable pieces and linked to what it names | per file |
| 6 | **Generate the documentation** | Admin | Docs page → *Generate Docs* | Routine summaries, modules, overview, data dictionary, glossary, interfaces, business rules, sequence diagrams. Sections are listed by title | 3–15 min (grows with routine count) |
| 7 | **Generate the Requirements pack** | Admin | Docs page → *Requirements pack* | Capability map, process flows, numbered functional requirements, non-functional requirements, with a coverage figure for business rules and capabilities. If it already exists you are asked before it is replaced, with *Only generate what's missing* as the alternative | 3–11 min |
| 8 | **Extract a spec** | Engineer | Open a routine → *Extract spec* | A spec in business terms: inputs and outputs, every rule as an invariant, side effects, edge cases and open questions, each citing code lines. For workbook code, inputs and outputs are named from the workbook's own cells and labels | ~1 min |
| 9 | **Review and sign** | SME | Spec review page | Accept or edit each claim, answer each open question, then **Sign**. Signing is blocked until every claim is handled | minutes (the people part) |
| 10 | **Generate the code** | Engineer | Spec review page → pick the target stack → *Generate scaffold* (opens under **Generated code**) | A package of ~24 files: for **React + Python**, a FastAPI back end (typed models, service, routes, pytest tests) and a React + Vite + TypeScript form with Jest tests | ~2–3 min |
| 11 | **Validate** | Engineer | Generated code → the scaffold → **Validation** | Run **Compile** (back end imports, front end builds), then **Generate test pack** and **Test pack** (pytest + Jest, one test per signed claim). Green counts, e.g. "All 34 tests passed (29 backend + 5 frontend)" | ~2 min each |
| 12 | **Repair if a gate fails** | Engineer | The routine's conversation thread | Type *"Regenerate `<routine>` with the last gate's errors fixed."*, press Confirm. The agent reads the failure, regenerates with it in hand, then run the gates again | ~3 min |
| 13 | **Final check** | any | Validation page | Compile **PASSED** and Test pack **PASSED**. For some source languages the page notes that equivalence testing is smoke-only | — |

## Rules of thumb

- **Run order for the clock:** steps 2–5 are quick; start step 6 or 8 early and talk while it runs.
- **Gates run inside the live API container.** Do not run them while someone else is using the system.
- **Do not open a spec's `/scaffold` address by hand.** It starts a generation (and spends model credits) the moment it loads. Use the **Generate scaffold** button.
- **A failure always says why.** A docs stage or a generation that fails reports the provider's reason on the page and in the thread.
- **One extraction per routine at a time.** Opening Extract on a routine that is already extracting says so and opens the routine when it finishes.

## What "validated" means, and what it does not

Validated means: the signed spec's rules are each covered by a test, the code builds, and every test passes. It does
not yet mean the numbers match the original system on real data. That needs an agreed set of cases run through both,
which is part of a real engagement. Lookup data held in hidden sheets is not moved into a database by this step
either.
