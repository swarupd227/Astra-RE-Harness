# Artefact adapters and the entity graph

*What each kind of attached file is cut into, how a piece is located, what facts an adapter states, and the
exact shapes the scheduler and package adapters read. `CLAUDE.md` carries the short version; the UX rules for
citing live in `docs/02_UX/agentic-ux-philosophy.md`.*

## The contract

An adapter (`Evidence/Adapters/`, one pure class per kind) takes a file's text or bytes and returns:

- **chunks** — the pieces that can be cited, each with a *locator* (where in the file) and, where there are
  lines, the lines it occupies;
- **facts** (some kinds) — the things the file says exist and how they relate, each pointing at the chunk that
  shows it;
- **warnings** — plain sentences about what could not be read, and a one-line **summary** of what was.

Adapters never throw and never touch a database. A file an adapter cannot read is kept as line windows with a
warning, not refused. `EvidenceAdapters.Finish` gives every adapter's output the same checks: no empty chunks,
text clipped at 20,000 characters, locators unique within the file (`~2`, `~3` for a repeat) and bounded to the
column, and a cap on chunks per file.

| Kind | Chunk | Locator | Facts |
|---|---|---|---|
| Source code | a routine | `L120-168` | (C# only) classes, web endpoints, queues; reads, calls, writes, configures |
| SQL (`.sql`, `.ddl`) | a statement that defines something, comments above included | `L10-42` | tables, views, procedures, functions; reads, writes, calls, references |
| Job schedules (`.xml`) | a plan header element, each task, a whole job | `xpath:/JOBP/Tasks/Task[3]` | jobs and plans; contains, precedes, runs, reads, writes, triggers |
| ETL packages (`.dtsx`) | each connection, variable and task | `xpath:/Executable/Executables/Executable[2]` | the package; what its data flow reads and writes, what it runs, which settings it names |
| Configuration (`.json`) | a setting | `json:$.Ofac.TimeoutSeconds` | one `config-key` per setting, with a value per file |
| Transcripts (`.vtt`, `.txt`) | a speaker's turn | `t=00:14:32-00:15:40` | — |
| Tickets (`.csv`) | a row | `row:INC-T-0412` | — |
| Documents (`.docx`, `.md`) | a section | `h=Restart` | — |
| Documents (`.xml`) | an element | `xpath:/Module/Screens/Screen[2]` | — |
| Spreadsheets (`.xlsm`, `.xlsx`, `.xls` …) | a sheet, named range, input rule, control, event, link out; per sheet the formulas that reach other sheets | `sheet:Quote`, `control:Quote/cmdStart`, `input:Quote!B4` | workbook, sheets, named ranges, inputs, controls, macros, VBA modules, data connections; contains, reads, writes, runs, triggers, references |
| Audio | none — it plays with a transcript | — | — |

A short id (`SQL-3fa9c01e`) is a hash of the file path and the locator, so the same piece has the same id on
every rebuild and in every environment.

## The entity graph

Facts are stored per file (`file_facts`). The graph (`graph_entities`, `graph_links`) is the **merge** of every
file's facts for a source version, rebuilt after any attach, replace, remove or re-sync — never patched.

- A thing is identified by **type and name**, case-insensitive, brackets ignored (`TLR_STAGE` = `[tlr_stage]`;
  `dbo.` is dropped). A job that runs `usp_PostStage` and the script that defines it are one procedure.
- A thing only a link names is kept with `declared: false` ("referenced, not defined in the evidence"). This is
  what a gap finder looks for.
- A reference to a *table* also finds a *view* of that name; a reference to a *procedure* finds a *function*.
- A link stated twice is one link, with the higher confidence and both pieces of evidence.
- **Confidence**: `high` for what a rule read straight from the evidence (a parsed statement, an explicit
  command); `medium` for a pattern match (SQL ScriptDom could not read, a table named inside a command-line
  query, a string in a script that looks like a setting). `origin` is `rule` today; a model's proposals will
  be `model`.
- Types: `table view procedure function job package file script config-key class endpoint queue`. Relations:
  `reads writes calls runs precedes contains triggers configures references`.

API (open to every persona): `GET /api/v1/corpora/{id}/entity-graph?type=job,table`,
`GET /api/v1/corpora/{id}/entities?type=&q=&limit=&offset=`,
`GET /api/v1/corpora/{id}/entities/{type}/{name}` (a name may be a path),
`GET /api/v1/corpora/{id}/inventory` (see below).

### Evidence for a fact that points at lines

A fact read from code names the lines it was found on (`L45-47`), not a chunk. The graph shows it by the
**smallest routine that contains those lines**, else the routine it overlaps most, else the routine that starts
within 30 lines below (a class header sits just above its first method). Where no routine can show it
(top-level statements, a field initialiser, a routine the parser missed) a chunk of just those lines is made when
the facts are stored (`text_hash` starts `gap:`, kind `code`), so every fact can be cited. Those chunks belong to
the facts: they are replaced whenever the file's facts are read, and `BuildCodeAsync` leaves them alone.

## C# (code facts)

Read with Roslyn, syntax only — no compilation, nothing is run (`Evidence/Code/`). The whole set of a version's
`.cs`/`.csx` files is read together, in two passes, because some facts only make sense across files; one changed
file means all are read again, which is fine because a version's code only changes with an ingest or a re-sync.
Facts are stored per file like the artefacts' (`file_facts`, one row per C# file, empty if nothing was found) and are
built at ingest/re-sync after the routines are indexed, or on first read for a project that predates them.
Limits: 1,000,000 characters per file, 40,000,000 in all (the rest is skipped with a note).

Pass 1 (`RegistryWalker`) collects what only the project knows: which settings section each options class is bound to
(`Configure<T>(config.GetSection("X"))`, `AddOptions<T>().Bind(…)`, `.BindConfiguration("X")`,
`GetSection("X").Get<T>()`), the base address and timeout each registered HTTP client gets
(`AddHttpClient<T>(c => c.BaseAddress = …)`, named clients, `.ConfigureHttpClient`), the properties of each type and
the text constants. Pass 2 (`FactWalker`) reads each file against that.

What is stated, per **class** (a class appears only if it touches something outside itself; its name is
namespace-qualified, nested types included; top-level statements are the class `Program`):

| Found | Entity | Link from the class | Notes |
|---|---|---|---|
| a setting read | `config-key` (named as the app reads it, `A:B`; `env:X`; `ConnectionStrings:N`) | `reads` | indexer, `GetValue`, `GetSection(…)[…]`, `GetConnectionString`, `ConfigurationManager.AppSettings`, `Environment.GetEnvironmentVariable`, and `options.Value.Prop` through a bound options class (high; medium if the receiver is only recognised by its name) |
| a web call | `endpoint` named `VERB {Setting:Key}/path` or `VERB https://host/path` | `calls` (high) | `HttpClient` verbs, `HttpRequestMessage`, RestSharp `RestRequest`; the setting is also `configures` the endpoint (`role: base-address`/`timeout`); a literal fallback is kept as `fallbackUrl` |
| a message queue | `queue` (exchange or queue, name literal or `{Setting:Key}`), `broker` | `writes` (publish) / `reads` (consume) / `references` (declare) | RabbitMQ `BasicPublish`/`BasicConsume`/`QueueDeclare`/`ExchangeDeclare`, Service Bus `CreateSender`/`CreateProcessor`/`CreateReceiver` |
| SQL text | `procedure`, `table` | `calls` / `reads` / `writes` | `SqlCommand("…")`, `CommandText = "…"`, Dapper/EF raw SQL; a bare name is a call only where the member says `CommandType.StoredProcedure`; names inside SQL text are medium confidence |

Every link keeps its **sites** (`attrs.sites`: routine, line, and where it applies `retry: loop|policy`,
`timeoutSeconds`, `timeoutSource: code|setting`, `timeoutKey`, `form`, `routingKey`). A timeout the code writes as a
number, next to a setting that was meant to hold it, is how "the setting is ignored" shows: the setting has no `reads`
link and the endpoint says `timeoutSource: code`.

What it deliberately does not do: it does not guess. A web call whose address is built at run time, or a setting read
whose key is, is counted and reported as a note (`… were not read`), never turned into an entity. It is not a semantic
analysis: a name is followed back through locals, fields, constants and a few helpers (`TimeSpan.FromSeconds`,
`Uri`, `string.Format`, interpolation, `??`), not through calls.

## PHP (code facts, read by the parser sidecar)

Read with the real PHP grammar (nikic/PHP-Parser 5, `app/parser-sidecar/php_tools/php_scan.php`, run by the PHP CLI in
the sidecar image) — the code is parsed, never executed. The scanner states only what the code literally contains:
routines, calls resolved through `use` imports, typed and promoted properties, locals assigned from `new`, and
`$this`/`self`/`static`/`parent`, plus neutral **sites** (calls with their literal arguments, SQL strings, superglobal
reads, includes, `switch` dispatch). Small **detectors** (`parser_sidecar/php_facts.py`) read those sites; adding a
framework is adding a detector, nothing else. The API asks for the facts with the `ExtractFacts` RPC (one call for all
of a version's `.php` files, from `EvidenceService.BuildCodeFactsAsync`) and stores them like the C# facts.

| Found | Entity | Link | Detector |
|---|---|---|---|
| a route the application serves | `route` named `VERB /path` (`GET /invoices/{id}`, `GET\|POST /search`, `ANY /x`) | `triggers` → the handler `class` (`attrs.method`) | `Route::get/post/…/match/any`, `Route::resource/apiResource` (Laravel); `$router->…` (Lumen), `$app->…` (Slim), `$routes->…` (CodeIgniter 4); `#[Route]` on methods with a class prefix (Symfony and alike) |
| a front controller's dispatch | `route` named `ANY /script.php?param=value` | `triggers` → the `script` | `switch ($_REQUEST['action'])` (high), `switch ($action)` where the variable's name says what it is (medium) |
| SQL text | `table` | `reads` / `writes` | any string or concatenation that starts with a SQL verb (medium when concatenated) |
| a query builder or active record | `table` | `references` (`DB::table`), `reads`/`writes` (`$this->db->get/insert/…`), `contains` (`Schema::create`, which also declares it) | Laravel, CodeIgniter 3 style |
| a setting read | `config-key` | `reads` | `env()`, `getenv()`, `config()`, `ini_get()`, `Config::get()`, `$_ENV[…]` |
| an include | `file` | `references` | `include`/`require` with a literal path |

The actor of a link is the class the code is in, or the `script` (the file path) for a function or the file's own
statements. A route is an `route`, not an `endpoint`: the inventory counts **routes served** (`routes`) apart from
**endpoints called** (`endpoints`). Not yet read: route group prefixes (`Route::prefix('api')->group(…)`), Eloquent
models' implicit table names, and routes built from variables; these are gaps, not guesses.

**Routes feed extraction.** When a routine is extracted, `ServedRoutes` (Llm/, pure) matches it against the version's
route → `triggers` links: a link to a `class` names the handling method (`attrs.method`, or `__invoke`), a link to a
`script` stands for the file's own statements (the routine named after the file — the *dispatcher*) and lists in
`attrs.calls` the routines each case calls (the *handlers*). The matches go to the model beside the neighbourhood
("Routes this routine serves") and onto the extract stream as `routes_attached`, so the spec's `http_contracts` take
their method and path from the route table rather than guessing them from the handler's body. Nothing here is PHP
specific: any language whose facts state routes this way gets the same.

## Linking rules

`LinkingRules.Apply` runs on the merged graph (after the merge, before it is stored) and only adds what a rule can
justify; every link or attribute it adds carries `rule`, and links are `origin: rule`.

- **Setting ↔ address ↔ endpoint.** An endpoint whose base is a setting gets `resolved` — that setting's value in every
  attached configuration file. An endpoint with a literal base address is `configures`-linked (medium, `rule:
  config-url`) from a setting that holds the same address. A queue named by a setting is linked from it (high,
  `config-name`) and resolved the same way; a literal queue name equal to a setting's value is linked medium.
- **Mentions.** A ticket row, a transcript turn or a document section that names a thing is listed on that thing as
  `mentions: [{evidenceId, kind, via}]` with `mentionCounts` per kind (at most 25 kept per kind). It is kept apart from
  the thing's own `evidenceIds`, which are structured: what people said has to be checked against what the files show.
  A thing is named by its name, by its last part (`dbo.TLR_STAGE`, a class without its namespace), for a table by a
  constraint it owns (`via: constraint UQ_…`), and for a ticket also by its `cmdb_ci` / configuration item column
  (`via: cmdb_ci`, an exact match, any name). A name is only looked for in text when it is *distinctive* (an underscore,
  digit, colon, a dot between names or the capitals of a compound name; 5+ characters), so the word "Drawer" does not
  mention a table of that name. Tickets and transcripts are read up to 30,000 pieces of 4,000 characters.

Narrative evidence changes the graph, so attaching, replacing or removing a ticket, transcript or document rebuilds it.

## Inventory

`GET /api/v1/corpora/{id}/inventory` counts what a project's evidence holds, each count checkable by listing the
entities of that type: files and chunks by kind; routines and languages in the code; things in the graph (declared, and
only referenced); and the **measures** — `tables views procedures functions jobs packages config-keys classes endpoints
queues routines interfaces` — each beside the count the project's settings expect (`expectedCounts[key]`). An expected
count with no measure of that name is returned with `found: null`, not dropped. `interfaces` is an upper bound, not the
interface map: endpoints, queues and files a job, package or class reads, writes or is triggered by.

Findings (only listed when there is something to say):

| Code | Says |
|---|---|
| `referenced-not-defined` | a job, procedure, table, view, function or package is named by something and defined by no attached file |
| `setting-not-configured` | code reads a setting that no attached configuration has |
| `setting-never-read` | a configuration setting no attached code or package reads (only said once there is code or a package that could have) |

## SQL

Read with ScriptDom (T-SQL). A batch it cannot read is split at the next `CREATE`/`ALTER` and each piece tried
again; a piece it still cannot read is read by pattern (names, keys, the tables after `FROM`/`INSERT`/`EXEC`)
and flagged `parsedBy: pattern`. Parsing is bounded (10 s per file, 400,000 characters per batch); the rest is
read by pattern, with a warning. A table's chunk carries its columns, primary/unique keys and foreign keys; a
procedure's carries its parameters, what it reads, writes and calls, and whether it uses a transaction, a
cursor, a loop or TRY/CATCH (stated both ways: "no transaction" is a fact too).

## Scheduler exports (UC4 / Automic style)

These are **our** shapes, read by rules that tolerate the usual variants. When real exports arrive, adjust
`JobAdapter` (an afternoon, not a rewrite); the graph and everything above it do not change.

A **job plan**:

```xml
<JOBP name="TLR_EOD">
  <Title>Teller end of day</Title>
  <Host>UC4AGENT-TLR01</Host>
  <Schedule>Mon-Fri 20:30</Schedule>
  <Notify onError="teller-ops@brfc.example" page="oncall"/>
  <Tasks>
    <Task Num="1" Job="TLR_EOD_LOCK"/>
    <Task Num="2" Job="TLR_EOD_COUNT" After="1"/>
    <Task Num="3" Job="TLR_EOD_POST" After="2" Restart="true"/>
  </Tasks>
</JOBP>
```

- Each header element is a chunk (so the schedule is cited by the schedule); each `Task` is a chunk, however
  small. `After` takes task numbers or job names (`"1"`, `"A, B"`); `Restart="true"` marks the restart point
  (or a header `<Restart task="3"/>`). Header values may also be attributes (`<JOBP name=".." host=".." schedule="..">`).
- Facts: the plan `contains` each job (link attrs `task`, `restartPoint`), a task `precedes`-links from each
  predecessor, and the plan entity carries `schedule`, `host`, `notify`, `pages`, `restartTask`, `restartJob`,
  `tasks`, `taskCount`. The jobs a plan names need not be attached; they are kept as referenced-only.

A **job**:

```xml
<JOBS name="TLR_EOD_POST" type="windows">
  <Title>Post staged teller transactions</Title>
  <Host>UC4AGENT-TLR01</Host>
  <Script><![CDATA[
sqlcmd -S %TLR_SQL% -d TELLER -b -Q "EXEC dbo.usp_PostStage"
]]></Script>
</JOBS>
```

- Root `JOBS…` (`JOBS_WIN`, `JOBS_UNIX`) or `JOBP`, optionally inside `<uc-export>`. A file whose root is neither
  is chunked as plain XML with the warning "does not look like a scheduler export".
- The command text (`Script`, `Command`, `MSCRI`, `CommandLine`) is read for: `EXEC proc` → *runs* a procedure
  (high); `dtexec /F pkg.dtsx` (or `/SQ`) → *runs* a package; `bcp … queryout|out path` → *writes* a file,
  `bcp … in path` → *reads* one; a `.ps1/.bat/.cmd/.vbs/.py/.sh` path → *runs* a script; tables inside a
  `-Q "…"` query or a `bcp` query → *reads/writes* a table (medium).
- `<Trigger type="file" path="…"/>` (or `FileEvent`) → the file *triggers* the job.

## Integration Services packages

`<DTS:Executable>` root, as SSIS writes it (any namespace prefix). Read: `ConnectionManager` (flat files give a
path; OLE DB gives `server/database`; a connection set by a property expression takes the value of the variable
it names), `Variable`, each task (`Execute SQL`, `Data flow`, `Script`, …). A **data flow** component whose class
contains `Source`/`Destination` yields *reads*/*writes* of the connection's file or the table in `OpenRowset` /
`TableOrViewName`; an **Execute SQL** task yields *runs* of the procedures it executes and (medium) the tables it
touches; a **Script** task's source (`ProjectItem`) is searched for strings shaped like a setting
(`"Reconciliation:VaultFilePath"`), giving a medium-confidence *configures* link from the config key to the package.
Nothing is executed or evaluated.

## Spreadsheets (and VBA in Office documents)

Read statically from the package (`Office/WorkbookSurface`, `Office/OfficeVba`): nothing is calculated, no
macro runs, no Office is involved. A workbook's pieces are an overview (`workbook`), each sheet (code name,
visibility — a *very hidden* sheet is only reachable from code — used range, value and formula counts), each
defined name with the text beside its cell as its label, each data-validation rule (list items or bounds, label,
prompt; both the plain and the x14 extension forms), each control (form controls from the VML, ActiveX controls by
their Forms 2.0 class id, shapes with a macro) with the macro it runs checked against the VBA project, each
workbook/sheet event handler (`Workbook_Open`, `Worksheet_Change`, `Auto_Open`), what each VBA module names
(sheets, names, cells) and each link out (other workbooks; data connections with credentials removed). Per sheet,
the formulas that read another sheet, a name, a table (`mortality[age]`) or another workbook are one piece.

Facts are named under the workbook's path (`Rating.xlsm/Quote`, `Rating.xlsm/Pricing.Quote`): the workbook
`contains` sheets; a sheet `contains` its named ranges, inputs and controls and `reads` the sheets and names its
formulas use (attr `formulas`: how many); a control `runs` its macro (high when the routine is in the project,
medium when not) and `writes` its linked cell (to the named range on it where there is one); the workbook or a
sheet `triggers` an event handler; a VBA module `contains` those macros and `references` (medium) what its code
names; the workbook `reads` linked files and data connections. A macro carries `codePath` — the source file it is
in once the workbook is uploaded as code.

**At intake**, an Office document with macros given as source (an upload, a file in a zip, a re-sync, a cloned
repository) becomes its modules, `Rating.xlsm/Pricing.bas|.cls|.frm`, parsed as VB; document modules with no code
are left out and the result says what was read. A spreadsheet is also kept whole: once the version is parsed, the
run switches the Spreadsheets kind on and attaches it, so code, evidence and graph come from one upload.
**Legacy `.xls`** (BIFF8, `Office/LegacyWorkbook`, [MS-XLS]) gives the same surface as `.xlsx`, read from the
`Workbook` stream: sheets (visible / hidden / very hidden, VBA code name, used range, value and formula counts),
defined names with the label beside their cell, formulas (rendered as Excel shows them — `Sheet2!$A$1`, `SUM(A1:B2)` —
by `Office/BiffFormula`, shared and array formulas listed once at their first cell), data-validation rules (a literal
list is one string token with its items NUL-separated; ranges and bounds as in `.xlsx`), and form controls: kind,
caption (the TxO text), the macro each runs (FtMacro → a defined name flagged as a procedure, matched to the VBA
project), the linked cell or input range, and the shape's own name from the drawing's `wzName` property (else
`Button <id>`). An unreadable record is a warning and the rest is kept. Not read: Excel 4 macro sheets, ActiveX
controls on sheets (a warning says so; their code handlers still appear through the VBA), Excel 5/95 (BIFF5) books,
and formulas past a token the renderer does not know (listed with a "not fully readable" note). Verified against
calamine's published expectations for Excel-written files and, for controls, a LibreOffice-converted copy of a
workbook whose `.xlsx` is read by the other reader (names, controls, linked cells and macros agree).

**UserForms** (`Office/UserForms`, [MS-OFORMS]): each form's designer storage is read for its controls — kind,
name, caption, tab order, position and size in points, design-time value, option group, `ControlSource` and
`RowSource` — following Frames and MultiPage pages (nested `i<id>` storages). Every record declares its size and the
reader checks it consumed exactly that; a mismatch is a warning and the form is marked incomplete, never laid out
wrong. A form is one piece (`form:frmQuotation`) listing its controls with each input's label (the Label overlapping
its first line, nearest on the left; else just above). Facts: the workbook `contains` the `user-form`, which
`contains` its controls (a Frame or page contains its own); a control `runs` the handlers its form's code defines
for it (`cmdNext1_Click`), `writes` its `ControlSource` cell and `reads` its `RowSource` range. A custom ActiveX
control (one in the form's class table: a date picker, tree view, list view …) is listed with its name, tab order and
position, its kind taken from its CLSID (`DatePicker`, `TreeView`, … — an unknown class is `ActiveX {clsid}`, never a
guess), and its handlers; its size and settings live in its own opaque data and are not read. A date picker takes
the label beside it like a text box does.

## Configuration

One chunk per setting, located by JSON path and named as the application reads it (`Ofac:TimeoutSeconds`). A list
of up to 12 plain values is one chunk. **A secret-looking value never enters a chunk or a fact**: a key ending in
password, passwd, pwd, passphrase, secret, token, apikey, privatekey, accesskey, accountkey, sharedkey or
credential(s), or `Password=…` inside a connection string, is replaced by `<<MASKED>>` (placeholders such as
`<<SECRET>>` or `${VAR}` are kept). The same rule hides the value in the file lines the evidence panel shows
(`ConfigAdapter.RedactLine`); the stored file is untouched.

## Adding an adapter

1. Write the adapter (text/bytes in, `AdapterResult` out) and register it in `EvidenceAdapters.AdapterFor`.
2. If it states facts, add the kind to `EvidenceAdapters.ProducesFacts` (its files then always get a facts row,
   and files indexed before the adapter existed are indexed again on first read).
3. Facts name their chunk by locator; use `XmlFacts.ChunkFor` for XML so a fact lands on the nearest chunk.
4. Tests: pure xunit on the adapter, a merge test if the facts join other files', and a Playwright spec through
   the API.
