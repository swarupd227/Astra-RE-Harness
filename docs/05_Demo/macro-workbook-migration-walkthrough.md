# Walkthrough: migrating a macro-enabled workbook to a web application

*A 15-minute live demo of one workbook, end to end: what is inside it, what it does in business terms, and
generated Python + React code that builds and passes its tests. Everything here is a generic platform
capability — the workbook is whatever you upload.*

## Before you start

- **Pick the workbook.** Any `.xlsm`/`.xlsx`/`.xls`, ideally one with a button or two, a hidden lookup sheet,
  named input cells and (for the forms beat) UserForms. A quote or pricing template is ideal.
- **A real model is needed for steps 5–8** (the spec and the generated code). Local runs use the mock provider,
  which only proves the plumbing. On Azure the key is set by an Admin in **Platform → LLM**.
- **Run order matters for the clock:** upload and evidence are seconds; extraction takes ~1 minute; scaffold ~2
  minutes; the two gates ~2 minutes each. Start step 5's extraction while you talk through step 4.
- **The gates run inside the live API container.** Don't run them while someone else is using the system.
- **Don't open a spec's `/scaffold` address by hand** — that page starts a generation the moment it loads (and
  spends model credits). Reach it through the **Generate scaffold** button.
- Personas: **Engineer** uploads, extracts, generates and runs gates; **SME** reviews and signs. Switch with
  the persona menu (top right) at steps 6 and 7.

## The beats

| # | Do | You will see | Say |
|---|---|---|---|
| 1 | **Projects → New project**, drop the workbook, name it, upload. | "N notes on this ingest": "Read N VBA modules from *Book.xlsm* …", "Kept *Book.xlsm* as evidence: N citable pieces …", and a line per workbook with no macros. | "One file in. The code and the workbook both come out — nothing was opened in Excel, no macro ran." |
| 2 | Open the project page. | Files named `Book.xlsm/Module1.bas`, `…/frmQuote.frm`, each with its routines and line ranges. | "Every routine says which workbook it came from." |
| 3 | **Evidence** (project page → *Browse evidence*), filter **Spreadsheets**. | Pieces for the workbook, each sheet (a *very hidden* one is called out), each named cell with the label beside it, each button with the macro it runs, workbook events, data connections. | "A template's logic is split between its cells and its code. Both are cited here. The hidden sheet is where the rates live." |
| 4 | Open a **form** piece (`form:…`). | A tree: pages, frames, every control with its caption, each input's label, and the handler each button runs. | "This is the dialog the user fills in — fields, labels, order — read from the file, not recreated by hand." |
| 5 | Open a **routine** (a button's macro) → **Extract spec**. (Engineer) | A spec in business terms: inputs and outputs named by the workbook's own cells and labels, every lookup and formula as an invariant, writes to sheets as side effects, every claim cited to code lines. | "It reads the routine *with* its workbook, so `Range("Premium")` becomes 'Annual premium, written to C12'." |
| 6 | Review the claims; **sign**. (SME) | Each claim accepted or edited; open questions resolved; a signature. | "Nothing is generated from an unreviewed spec. The SME signs what the code must do." |
| 7 | On the spec review page choose target **React + Python** and press **Generate scaffold · React + Python**. (Engineer) | ~24 files: a FastAPI back end with input rules in typed models, a service, routes and pytest tests; a React + Vite + TypeScript form with Jest tests. | "The form's fields became a typed API and a web form, and every test is tied to a signed claim." |
| 8 | **Validation**: run **Compile**, then **Test pack**. | Compile: back end imports cleanly, front end builds. Test pack: pytest + Jest counts, one test per signed claim. | "It doesn't just look right — it builds and passes." |
| 9 | If a test fails: in the thread, *"Regenerate `<routine>` with the last gate's errors fixed."* Confirm; re-run both gates. | The agent reads the failure, regenerates with it in hand, and the next run passes. | "A failure is a conversation, not a ticket." |
| 10 | Close. | — | See "What a real engagement adds". |

## Known limits — say them before they are asked

- The **entity graph** (buttons → macros → sheets → names → forms) is built and stored for every workbook but has
  no screen yet; it is available through the API (`/api/v1/corpora/{id}/entity-graph`). Show the evidence pieces instead.
- A form's **page tab captions** (the text on a MultiPage's tabs) are not read; pages show their internal names.
- **Legacy `.xls`** workbooks give their VBA, but their sheets, named cells and buttons are not read yet.
- **Custom ActiveX controls** on a form (a class table in its designer data) are flagged, not laid out.
- Calculation **formulas on the sheets** are cited and counted but not evaluated; business rules that live only in
  sheet formulas surface as the spec's open questions unless a macro touches them.
- The generated **React + Python** package is the reference shape for a form-driven calculation. A workbook with
  a very different shape (a long batch process, heavy charting) produces a weaker first draft and more repair rounds.

## What a real engagement adds

Per-estate rating tables moved from hidden sheets into a database; authentication and audit around the generated
API; the workbook's remaining forms and macros run through the same loop in bulk; equivalence testing against
the workbook's own outputs on a set of agreed cases (the platform runs the signed claims; agreed cases make the
numbers match to the cent).
