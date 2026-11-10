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
| Source code | a routine | `L120-168` | (from the parser) |
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
- Types: `table view procedure function job package file script config-key`. Relations:
  `reads writes calls runs precedes contains triggers configures references`.

API (open to every persona): `GET /api/v1/corpora/{id}/entity-graph?type=job,table`,
`GET /api/v1/corpora/{id}/entities?type=&q=&limit=&offset=`,
`GET /api/v1/corpora/{id}/entities/{type}/{name}` (a name may be a path).

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
