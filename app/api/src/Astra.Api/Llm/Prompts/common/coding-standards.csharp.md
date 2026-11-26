### C# / .NET

Security (the QUALITY gate fails the package on any of these):
- Never put a secret, password, connection-string password, API key or token in code or `appsettings*.json`.
  Read it from configuration or the environment.
- SQL is always parameterised (`SqlParameter`, Dapper parameters, EF Core LINQ or `FromSqlInterpolated`); never
  concatenate or `string.Format` a value into SQL, never `FromSqlRaw` with interpolated values.
- Never `BinaryFormatter`, `Process.Start` with user input, or `TypeNameHandling` other than `None`.
- Never disable certificate validation (`ServerCertificateCustomValidationCallback` returning true). Use `https`.
- `SHA256` or stronger for hashing and `RandomNumberGenerator` for anything security related.
- CORS lists the origins it allows; never `AllowAnyOrigin` with credentials.

Coding standards:
- Nullable reference types on; no warnings suppressed to make the build pass.
- Constructor injection of dependencies; `ILogger<T>` with message templates, never `Console.Write*`.
  Never log secrets or personal data.
- Never an empty `catch`; catch what you can handle and rethrow with `throw;`. Map errors to `ProblemDetails`
  with the right status code; never return an exception message from an unexpected error.
- `async` all the way with a `CancellationToken`; never `.Result` or `.Wait()`.
- `decimal` for money and rates, with the `MidpointRounding` the spec states.
- Request models validate the rules the spec states (data annotations or explicit checks).
- Keep methods short and simple (complexity under 15). Tests are xUnit, one behaviour per test.
