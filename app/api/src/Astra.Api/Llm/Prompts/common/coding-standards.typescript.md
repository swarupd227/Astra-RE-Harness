### TypeScript front end (React / Angular)

Security (the QUALITY gate fails the package on any of these):
- Never put a secret, API key or token in front-end code; the browser can read all of it.
- Never `eval`, `new Function`, or a string passed to `setTimeout`/`setInterval`.
- Never insert raw HTML (`dangerouslySetInnerHTML`, `[innerHTML]`, `bypassSecurityTrust*`) built from data.
- Call the back end through the configured base URL over `https`; never hard-code a host.

Coding standards:
- Strict TypeScript: no `any` (use a precise type or `unknown` and narrow it), no non-null `!` that hides a real
  null. Types for every request and response the back end defines.
- No `console.log` left in application code.
- Every input has a label, validation that mirrors the back end's rules, and an error message the user can act on.
  Show the back end's error message when a request fails, never a blank screen.
- Function components and hooks (React) or standalone components (Angular); no unused imports or variables.
- Tests assert what the user sees and what is sent to the back end.
