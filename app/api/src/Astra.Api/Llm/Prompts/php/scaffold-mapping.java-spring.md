These rules say how PHP becomes Java 21 / Spring Boot 3. They hold for every PHP routine, whatever the archetype.

**The HTTP surface (the spec's `http_contracts`) is a contract, not a suggestion.**
- Each contract is one `@RestController` method with the same method and path (`@GetMapping("/invoices/{id}")`). A
  front-controller action `ANY /api.php?action=getCustomer` keeps its URL: map `/api.php` with
  `params = "action=getCustomer"` (one method per action), accepting GET and POST when the contract says ANY.
- Return `ResponseEntity` with exactly the status of each response in the contract. PHP's implicit 200, a
  `header('Location: …')` 302, `http_response_code(…)`, `abort(404)`, `response()->json($x, 201)` and a framework's
  422 validation reply each become that explicit status. Do not let Spring choose: its default validation error is a
  400 ProblemDetail, which breaks callers — shape errors in the `@RestControllerAdvice` as the contract says.
- Body field names and JSON types stay as the contract states them (a PHP array key `customer_id` stays
  `customer_id` — use `@JsonProperty` when the Java name differs). An empty PHP array that callers receive as `[]`
  stays a JSON array, never `null`.
- Request parameters come in through `@PathVariable`, `@RequestParam`, `@RequestBody` (a record) or `@RequestHeader`,
  per the contract's `source`; `$_SESSION` values through an injected request-scoped bean or the security context,
  never a static.

**Access rules (`authorization_rules`).** A login check becomes an explicit check (or Spring Security, if the
archetype has it) with the same outcome on failure — a 302 to the login URL stays a 302, a 403 stays a 403. An
ownership check stays in the service and keeps its exact condition (often a `user_id = ?` in the SQL). A rule with
`mechanism: none` is kept absent: do not add a gate the legacy code did not have, and say so in a comment.

**Data access (`data_accesses`).** One repository method per statement, on `JdbcTemplate` with every value bound
(`?` placeholders). Never build SQL by concatenating a request value, even where the PHP did. Keep the table and
column names of the legacy schema. Statements the spec says commit together run in one `@Transactional` service
method. `mysqli_query` returning false, or an empty result the routine checks, becomes an explicit branch.

**PHP semantics.**
- Loose `==` becomes the explicit typed comparison the spec's `loose_type_coercions` give; `===` is `equals` on the
  same type.
- `isset` / `empty` / `??` follow the spec's `null_safety_contracts`; `empty("0")` is true — never turn `empty()` into a
  plain null check.
- An array is a `List`, an order-preserving `LinkedHashMap` or a record, as `array_shape_semantics` decides.
- Money is `BigDecimal` (scale and rounding as the spec states), never `double`; integer overflow is `long`.
- A function that returns false on failure, and `@`-suppression, become typed exceptions or an explicit branch, as
  `error_handling_contracts` say.

**Tests.** Every response of every contract gets a MockMvc test asserting its status code and the body fields that
matter; every data access gets a repository test on the embedded database; business rules get plain unit tests.
Keep the archetype's test structure and count, renamed to this routine's resource.
