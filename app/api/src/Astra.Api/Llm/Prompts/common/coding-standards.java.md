### Java / Spring Boot

Security (the QUALITY gate fails the package on any of these):
- Never put a secret, password, API key, token or private key in code or config. Read it from configuration
  (`@Value("${...}")` / `@ConfigurationProperties`) with no default value that looks like a real secret.
- SQL is always parameterised: `JdbcTemplate`/`NamedParameterJdbcTemplate` with `?` or `:name` placeholders, or JPA
  parameters. Never build SQL by concatenating or formatting a value into the string.
- Never pass user input to `Runtime.exec`/`ProcessBuilder`, `ObjectInputStream.readObject`, `XMLDecoder`, or a
  scripting engine. Parse XML with external entities disabled.
- Never disable TLS checks (trust-all `TrustManager`, `HostnameVerifier` returning true). Call outside services over `https`.
- Use `SHA-256` or stronger for hashing and `SecureRandom` for anything security related; never MD5/SHA-1 for security.
- CORS lists the origins it allows; never `*` together with credentials. Actuator exposes `health` and `info` only.

Coding standards (Checkstyle, PMD and SpotBugs run on every package):
- Constructor injection only: `private final` fields set by the one constructor; never `@Autowired` on a field.
- Every `@RequestBody` parameter is `@Valid`, and its type carries Bean Validation constraints (`@NotNull`,
  `@NotBlank`, `@Size`, `@Min`/`@Max`, `@Pattern`) for the rules the spec states.
- Errors leave through one `@RestControllerAdvice` that maps validation and domain exceptions to problem-detail
  responses with the right status code, when the reference package has one. Otherwise map them in the controller.
  Never return a stack trace or exception message from an unexpected error to the caller.
- Log with SLF4J (`private static final Logger log = LoggerFactory.getLogger(X.class)`), parameterised
  (`log.info("x={}", x)`); never `System.out`/`System.err` or `printStackTrace()`. Never log secrets or personal data.
- Catch the specific exception you can handle; never an empty `catch`, never `catch (Exception e)` that hides the cause.
  Rethrow with the cause attached.
- No wildcard imports, no unused imports, variables or private methods. Name constants `UPPER_SNAKE_CASE`.
- `BigDecimal` for money and rates, created from a `String` or `valueOf`, never `new BigDecimal(double)`. Compare
  with `compareTo`, and state the `RoundingMode` wherever you divide or set a scale.
- Use `equals` (or `Objects.equals`) to compare objects and strings, never `==`. Return empty collections, not `null`.
  Use `Optional` for a lookup that can find nothing.
- Keep methods short and simple (cyclomatic complexity under 15). Make immutable data `record`s or `final` fields.
- Tests use JUnit 5 and AssertJ/JUnit assertions, one behaviour per test, with a `@DisplayName` that says the rule.
