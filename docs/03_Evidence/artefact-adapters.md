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
