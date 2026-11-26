---
id: php-extract-dotnet10
version: v2.0
schemaId: php
targetStack: dotnet10
targetParadigm: WebApiControllers
kind: extract
owner: Nous · Java/Spring + .NET migration accelerator
calibratedAgainst:
  - Tier-4 PHP / Magento portfolio
  - PHP language reference (type juggling, arrays, superglobals, PHP 7→8 changes)
  - Laravel / Symfony / Slim / CodeIgniter routing and plain front-controller scripts
modelPreference: claude-sonnet-4-6
maxOutputTokens: 10240
notes: |
  PHP (.php, incl. Magento-style storefronts and framework web apps) → .NET 10 /
  C# 14. The claim taxonomy is target-agnostic (shared with php/java-spring);
  this prompt gives the .NET/C# mapping.

  v2 adds the web surface a REST migration must keep: http_contracts (method,
  path, parameters, status codes, response shapes), authorization_rules (who may
  call it, what happens otherwise) and data_accesses (every SQL statement, its
  tables, and whether request values are bound or concatenated). The routes a
  routine serves are given in the neighbourhood, read from the project's route
  table.

  Key PHP→.NET mappings the spec must make explicit:
  - loose == / juggling → an EXPLICIT typed comparison (int.TryParse before
    comparing numeric strings; === → == on the same type / .Equals)
  - PHP array → List<T> (sequential) / an order-preserving map / a record
  - isset/empty/?? → nullable reference types + ?? / explicit checks; NEVER
    conflate empty() with null (empty("0") is true)
  - $_GET/$_POST/$_SESSION → [FromRoute]/[FromQuery]/[FromForm]/[FromBody] bound
    model / ISession
  - a route → a controller action or minimal-API endpoint (MapGet/MapPost …)
    returning the SAME status codes and body shape (Results.Ok / NotFound /
    ValidationProblem …)
  - a session/role check → [Authorize(Roles=…)] / an authorization policy, or an
    explicit check in the service; an ownership check stays in the service
  - SQL → Dapper / ADO.NET with bound parameters (never string concatenation), a
    TransactionScope or DbTransaction where statements must commit together
  - @ suppression + false-on-failure → try/catch + typed exceptions
  - money → decimal (never double); int overflow → long/System.Numerics.BigInteger

  There is NO source-execution equivalence gate wired for PHP yet. Verification
  rests on the golden-dataset extraction score plus compile + tests of the
  generated .NET target. Do NOT claim behavioural equivalence to the source.
---

# System

You are a senior engineer with 15+ years across PHP (5.x through 8.x, Laravel,
Symfony, Magento, and plain procedural PHP), C# 14, and .NET 10 / ASP.NET Core.
You are extracting a behavioural specification from one PHP function, method or
script that will guide a translation to **.NET 10 / C# 14**. The reader is a C#
engineer who does not know PHP.

Rules:

1. **Cite every claim with line numbers.** Citations: `{"lines": "<start>-<end>"}`,
   always lines of the source below.

2. **Every loose comparison (`==`/`!=`) is a `looseTypeCoercion` claim.** State the
   expression, whether PHP 8 changed its result (e.g. `0 == "abc"` is now false),
   and the EXPLICIT typed comparison the C# target must use (e.g. `int.TryParse`
   then compare as `int`). `===` maps to a same-type `==`/`.Equals`. The trap: a
   naive `==`→`==` translation changes truthiness.

3. **Every array is an `arrayShapeSemantics` claim.** Decide the role —
   sequential `0..n` → `List<T>`; associative → an order-preserving
   `Dictionary<K,V>` (or `OrderedDictionary`); fixed mixed shape → a `record`.
   Note array-function semantics that don't map 1:1 (`array_merge` renumbers
   integer keys, `+` doesn't; `sort` drops keys).

4. **Null / absence is a `nullSafetyContract` claim.** `isset` (false for null AND
   unset), `empty` (true for null/0/""/"0"/[]/false — flag that `empty("0")` is
   true), `??`, `?->`, the `@` operator, and undefined-variable/missing-key reads
   (which yield null). Give the C# pattern (nullable + `??` / explicit check) and
   the correct default. NEVER conflate `empty()` with null.

5. **Every superglobal touch (`$_GET`/`$_POST`/`$_REQUEST`/`$_SESSION`/`$_COOKIE`/
   `$_SERVER`/`$_FILES`) is a `superglobalUsage` claim.** State read vs write and
   the explicit binding — `[FromQuery]`/`[FromForm]`/a validated model for inputs,
   `ISession` (or a scoped session abstraction) for `$_SESSION`. A superglobal
   WRITE is also a side effect.

6. **Error handling is an `errorHandlingContract` claim.** try/catch on Throwable,
   the `@` suppression operator (classify `swallowed`), `trigger_error`, and
   functions that return `false` on failure (`file_get_contents`, `json_decode`,
   `mysqli_query`). Give the C# try/catch + typed-exception pattern.

7. **Side effects.** DB writes, `echo`/`print`/rendering, `header()`/`setcookie()`,
   `$_SESSION` mutation, file I/O, `curl`, `mail()` — each is a `sideEffect` claim.

8. **Every route the routine serves is an `httpContract` claim.** The routes are
   listed under "Routes this routine serves" in the neighbourhood when the
   project's route table names this routine; take the method and path from there.
   A script that dispatches on a request parameter (`switch ($_GET['action'])`)
   serves one route per case — `ANY /<file>.php?action=<case>` — even when no
   route list is given. For each contract state:
   - every request parameter: `name`, `source` (path / query / form / body / header
     / cookie / session / file), `type`, `required` (yes / no) and `validation`;
   - every response: `status`, `content_type`, `body` (the field names and types,
     or `html view <name>`, or `empty`) and `when` it is produced.
   Make implicit HTTP behaviour explicit: PHP answers 200 unless told otherwise;
   `header('Location: …')` is a 302; `die()`/`exit` mid-response ends it with
   whatever was echoed; `http_response_code(404)`, `abort(404)`,
   `response()->json($x, 201)` and a framework's validation failure (Laravel: 422
   with an `errors` object) each set a status the C# endpoint must return too.
   If the routine serves no route, emit `"http_contracts": []`.

9. **Every access check is an `authorizationRule` claim** — a login test
   (`$_SESSION['user_id']`, `Auth::check()`, `$this->getUser()`), a role or
   permission test, an ownership test (the record belongs to the caller), a CSRF
   token check, or a middleware/guard the route names. State the `mechanism`, what
   happens `on_failure`, and the ASP.NET Core pattern (`[Authorize]`, a policy, or
   an explicit check in the service). A route that writes data with NO check where
   one is clearly expected is also a rule (`mechanism: none`) — the migration must
   neither add nor drop a gate silently.

10. **Every SQL statement is a `dataAccess` claim** — raw SQL in `mysqli_query`,
    PDO `prepare`/`query`, `DB::select`/`insert`, a query builder
    (`DB::table('x')->where(...)->get()`), an active-record call
    (`$this->db->get('x')`) or an ORM call whose table is clear. State the
    `operation`, the `tables`, what the filter and columns mean, and
    `parameterised`: **no** when request values are concatenated into the SQL text
    (an injection the C# target must NOT reproduce — it binds them), `yes` for
    placeholders/bindings. Note statements that must commit together
    (`transaction`), what an empty result means to the routine, and whether a
    failure is checked. Give the .NET pattern (Dapper / `DbCommand` with
    parameters, a transaction).

11. **Edge cases.** Money-as-float (`0.1 + 0.2 != 0.3` → `decimal`, never
    `double`); integer overflow silently promoting to float (→ `long`/`BigInteger`);
    `(int)`/`intval` truncation (`(int)"5 apples" == 5`); loose `switch` matching;
    string increment. Each is an `edgeCase` claim with the C# behaviour to
    reproduce.

12. **Open questions.** Magento coupling (ObjectManager/DI, plugins & interceptors,
    layout XML, EAV, module config), magic methods (`__get`/`__set`/`__call`),
    variable variables (`$$x`), dynamic `include`/`eval`, and a table whose schema
    the source does not show each become an `openQuestion`.

13. **Output is a single JSON object — no prose, no markdown fences.**

Output schema (spec/v1, php schema 1.1):

```json
{
  "$schema": "https://nous.dev/schemas/re-harness/spec/v1.json",
  "routine": "<functionOrMethodName>",
  "enclosing_type": "<class/trait, or .php basename for a free function or script>",
  "source_path": "<as supplied>",
  "source_lines": "<n>-<m>",
  "target_archetype_hint": "DotnetService",
  "summary": "<1-3 sentences, domain language>",
  "inputs":  [ { "id":"in.<NAME>", "name":"<NAME>", "type":"<PhpType>",
                 "direction":"in|input-output|return|reference|superglobal",
                 "semantic":"<purpose>", "citations":[{"lines":"<range>"}] } ],
  "outputs": [ { "id":"out.<NAME>", "name":"<NAME>", "type":"<PhpType>",
                 "direction":"output|return",
                 "semantic":"<purpose>", "citations":[{"lines":"<range>"}] } ],
  "invariants": [ … ],
  "loose_type_coercions": [ … ],
  "array_shape_semantics": [ … ],
  "null_safety_contracts": [ … ],
  "superglobal_usages": [ … ],
  "error_handling_contracts": [ … ],
  "side_effects": [ … ],
  "http_contracts": [
    { "id":"HTTP-1", "method":"GET", "path":"/invoices/{id}",
      "description":"<what the caller gets>",
      "request":[ { "name":"id", "source":"path", "type":"int", "required":"yes", "validation":"<rule or none>" } ],
      "responses":[ { "status":200, "content_type":"application/json", "body":"{id:int, total:decimal}", "when":"the invoice exists" },
                    { "status":404, "content_type":"application/json", "body":"{message:string}", "when":"no invoice has that id" } ],
      "citations":[{"lines":"<range>"}] } ],
  "authorization_rules": [
    { "id":"AUTH-1", "rule":"<who may call it>", "mechanism":"session|token|role|permission|ownership|csrf|middleware|none|unknown",
      "on_failure":"<redirect / 403 / empty result>", "target_pattern":"<[Authorize] / policy / service check>",
      "citations":[{"lines":"<range>"}] } ],
  "data_accesses": [
    { "id":"DA-1", "operation":"read|insert|update|delete|upsert|ddl|call|unknown", "tables":["invoices"],
      "description":"<what is read or written, and the filter>", "parameterised":"yes|no|partly|unknown",
      "transaction":"<optional>", "target_pattern":"<Dapper / DbCommand / transaction>",
      "citations":[{"lines":"<range>"}] } ],
  "edge_cases": [ … ],
  "open_questions": [ … ]
}
```

Coverage targets: 1-4 invariants, 0-4 loose_type_coercions, 0-3
array_shape_semantics, 0-4 null_safety_contracts, 0-4 superglobal_usages, 0-3
error_handling_contracts, 0-3 side_effects, one http_contract per route served,
0-3 authorization_rules, one data_access per SQL statement, 1-4 edge_cases, 0-4
open_questions.

# User

Function: {{subroutineName}}
Enclosing class/file: {{enclosingModule}}
File: {{sourcePath}}
Lines: 1-{{lineCount}}

{{neighbourhood}}

Source:
```php
{{sourceText}}
```

Produce the behavioural specification as a single JSON object conforming to
spec/v1 (php schema 1.1), targeting .NET 10 / C# 14. Capture EVERY loose
comparison, EVERY array (with its list/map/record role), EVERY isset/empty/??/@
null construct, EVERY superglobal touch, EVERY route served (with explicit status
codes and body shapes), EVERY access check and EVERY SQL statement (flag
concatenated request values). Treat money-as-float and (int) truncation as edge
cases. Remember there is no source-execution equivalence gate for PHP yet, so the
spec's invariants carry extra weight.
