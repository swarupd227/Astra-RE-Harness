# PHP → Java equivalence (PHP track P7)

The EQUIVALENCE gate for a PHP routine runs the **PHP source** and the **generated Java method** on the same inputs and
compares what each does, vector by vector. Typed as *"run equivalence on resolveQty"* (the `run_gate` tool, gate
`equivalence`), or the EQUIVALENCE card's button on the validation report; `POST /api/v1/scaffolds/{id}/validate/equivalence`
underneath, with an optional body `{"vectors": [{"named": {...}, "superglobals": {...}, "label": "..."}]}` for cases an
engineer adds.

## Pieces

| Piece | Where | Does |
|---|---|---|
| php sidecar | `app/php-sidecar` (PHP 8.4 CLI + CGI, FastAPI, port 51060) | `POST /run`: loads the PHP files as declarations only, runs the routine once per vector in its own `php-cgi` process, returns return value, echoed output, response code, headers, by-reference outputs, `$_SESSION` after, exception, warnings |
| vectors | `Validation/PhpEquivalence/PhpEquivalenceVectors.cs` | drawn from the **signed spec**: `generatorHints.examples` / `.inputs` on invariants and edge cases, `http_contracts[].request` parameters (placed in `$_GET`/`$_POST`/`$_COOKIE`/`$_SESSION`/`$_SERVER` as the contract says), typed `inputs`; plus engineer vectors; de-duplicated, at most 24 |
| traits | `PhpRoutineTraits.cs` | a database (spec `data_accesses`, or `mysqli_*`/PDO/`$wpdb`/`DB::` in the code), `$_FILES`, network or shell calls → the whole run is **SKIPPED** with the reason; `$_GET`/`$_POST` read with no request parameters in the spec → SKIPPED |
| Java target | `JavaTargetLocator.cs` | the method with the routine's name (snake_case read as camelCase), or one whose annotations/comments name the PHP routine; a class named like the PHP class wins a tie |
| Java run | `AstraEquivalenceHarnessTest.java` (embedded) | added to the package for this run only, run by the maven sidecar (`-Dtest=AstraEquivalenceHarnessTest`); binds inputs by parameter name (`@RequestParam` names too), by position, or as the fields of the one Map/record/bean parameter; constructor collaborators are stand-ins that throw when called |
| verdict | `EquivalenceComparer.cs` | PASSED / FAILED (with the first difference, e.g. `$.items[1]: PHP 2 ≠ Java 3`) / SKIPPED (the vector cannot run fairly on one side) / ERRORED |

The run is FAILED when any vector fails, PASSED when at least one passes and none fails, SKIPPED when nothing could be
compared fairly. Metrics carry every vector (`mode: "per-vector"`); the validation report shows them as a list.

## How the PHP side is isolated

- **Declarations only.** Each file is tokenized (`PhpToken`) and only `namespace`/`use`/`const`, functions, classes,
  interfaces, traits, enums, literal `define()`s and `if (!function_exists(...))` guards are kept. A script's own
  dispatch, includes, database connections and output never run. Classes load on demand through an autoloader (one
  snippet per class, written next to its source so `__DIR__` keeps its meaning).
- **One process per vector**, `php-cgi -n` with `memory_limit`, `max_execution_time`, hard rlimits (address space, CPU,
  file size), `open_basedir` = the run directory, `allow_url_fopen`/`allow_url_include` off, and every process, socket,
  network, mail and curl function disabled. The sidecar runs as an unprivileged user.
- **No `eval`.** Inputs are JSON-decoded values bound to parameters; the routine is found by Reflection.
- php-cgi rather than the CLI SAPI because the CLI drops `header()` calls; `-q` keeps them off stdout.

## Input binding (both sides alike)

1. Inputs whose names match parameters (case and `_` folded) are bound by name; other parameters take their defaults.
2. None match and the routine takes one array/Map-like parameter → the inputs are that parameter's keys/fields
   (`resolveQty($input)` reading `$input['qty']` ↔ `resolveQty(Map<String,String> input)`).
3. A required parameter with no value → the vector is SKIPPED (`missingInput`), never guessed.

## Limits (honest)

- Database, uploads, network: skipped here; the HTTP parity replay (P8) is where a routine that needs a database is
  compared (legacy app and Spring service against one database).
- `$_SESSION` writes are reported but not compared (the Java side has no session to read back).
- A PHP response code other than 200 is compared only when the Java method answers with a status (a `ResponseEntity`
  or an exception that carries one).

## Deploy

Azure Container App `php-sidecar` (image `astraharnessacr.azurecr.io/php-sidecar:<sha>`, internal ingress on 51060);
the API's `Validation__PhpEndpoint` points at it. Because it runs code, `/run` and `/inspect` require the header
`X-Astra-Sidecar-Token` whenever `PHP_SIDECAR_TOKEN` is set (a Container App secret); the API sends
`Validation__PhpToken`.

The e2e spec (`php-equivalence.spec.ts`, mock providers) needs the PHP java-spring archetype enabled for scaffolding;
the canonical PHP catalog archetype is `preview`, so the spec skips itself where the scaffold route gates it. Bump `php_sidecar/__init__.py` `__version__` with every change;
`GET /health` on the sidecar reports it and the PHP version.
