---
id: docs-exemplar-business-rules
version: v1.0
owner: Nous · Documentation generation
notes: |
  Exemplar of the business-rules catalogue form — three entries as the
  pipeline stores them (one DocSection each), grounded in the shape of
  Indy's IdSMTP.pas. Shown to the model as a cached system block after
  the style guide. The model is told not to copy paths, lines, or claims.
---

# Exemplar — business rules (three entries)

### ESMTP first, SMTP on refusal

IF the client is configured to use `EHLO` and the server answers with anything other than `250`, THEN the client repeats the greeting as plain `HELO` and continues the session as classic SMTP with no capability list [Protocols/IdSMTP.pas:L221–L235]. On the `250` branch the reply lines are parsed into the capability list that later drives TLS and authentication choices. A replacement must preserve the fallback — many appliance relays still refuse `EHLO` — and must preserve its consequence: after a `HELO` fallback no extension is offered, so the session cannot upgrade to TLS or authenticate even if the server would have allowed it.

### TLS is refused, not downgraded, when required

IF `UseTLS` is `utUseRequireTLS` and the negotiated capability list does not contain `STARTTLS`, THEN the client raises `EIdTLSClientSMTPNoSupport` and does not send mail [Protocols/IdSMTP.pas:L238–L243]. With `utUseExplicitTLS` the same situation continues in clear text. The socket is already open when the exception is raised; the session is abandoned, not cleaned up, and the caller's `Disconnect` sends `QUIT` on a connection the server considers unauthenticated. A replacement must keep the hard refusal for the required mode — silently sending in clear text is the failure the setting exists to prevent.

### No credentials means no authentication attempt

IF `Username` is empty, THEN `Authenticate` reports success without sending `AUTH`, whatever `AuthType` says [Protocols/IdSMTP.pas:L296–L299]. The design intent is to let one client class talk to both open relays and authenticated submission servers with a single code path; the consequence is that a missing password surfaces later as a relay rejection at `RCPT TO` rather than as an authentication error. A replacement that validates credentials up front changes the error the user sees, which some callers have come to depend on.
