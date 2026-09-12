---
id: docs-exemplar-module
version: v1.0
owner: Nous · Documentation generation
notes: |
  Exemplar of the module document form, grounded in the shape of Indy's
  IdSMTP.pas (Delphi, one class per unit). Shown to the model as a
  cached system block after the style guide. It models tone, density,
  sectioning, and citation placement — the model is told not to copy
  its paths, lines, or claims.
---

# Exemplar — module document

# IdSMTP.pas — SMTP client with EHLO negotiation and pluggable authentication

`IdSMTP.pas` is the outbound-mail client of the Indy protocol library. It owns one class, `TIdSMTP`, which opens a session to a mail submission server, negotiates the server's capabilities, authenticates when credentials are present, and hands a `TIdMessage` to the base class for transmission. Everything protocol-shaped that is not specific to session setup — the `MAIL FROM` / `RCPT TO` / `DATA` exchange, reply parsing, recipient bookkeeping — lives in `TIdSMTPBase` [Protocols/IdSMTPBase.pas:L60–L94]; this unit is the policy layer above it. The one thing to know before editing: `Connect` and `Authenticate` are separate steps, and `Send` assumes both have run [Protocols/IdSMTP.pas:L268–L281].

## What it provides

Callers use four methods. `Connect` opens the socket, reads the greeting, and runs the EHLO/HELO exchange [Protocols/IdSMTP.pas:L212–L247]. `Authenticate` selects a mechanism from `AuthType` and the advertised `AUTH` capabilities and returns whether the server accepted the credentials [Protocols/IdSMTP.pas:L292–L338]. `Send` and `SendNoDate` transmit a message; the second variant leaves the `Date` header untouched for relays that must not restamp [Protocols/IdSMTP.pas:L268–L290]. `Disconnect` sends `QUIT` when the peer is still connected and always closes the socket [Protocols/IdSMTP.pas:L249–L266].

## How it works

Session setup is a capability negotiation. After the greeting, `Connect` sends `EHLO` when `UseEhlo` is true and, on any reply other than `250`, retries with `HELO` and clears the capability list [Protocols/IdSMTP.pas:L221–L235]. The capabilities the server returns drive two later decisions: whether `STARTTLS` is offered, which `Connect` acts on immediately when `UseTLS` asks for it [Protocols/IdSMTP.pas:L236–L246], and which `AUTH` mechanisms are available, which `Authenticate` reads.

Authentication branches on `AuthType`. `satNone` returns true without sending anything. `satDefault` runs the legacy `AUTH LOGIN` exchange — two base64 lines — and, when `ValidateAuthLoginCapability` is set, refuses to attempt it unless the server advertised `LOGIN` [Protocols/IdSMTP.pas:L302–L318]. `satSASL` delegates to the `SASLMechanisms` collection, which tries each configured mechanism in order until one succeeds [Protocols/IdSMTP.pas:L320–L336]. In every branch an empty `Username` short-circuits to success, so an unauthenticated relay and a misconfigured client look identical from the caller's side [Protocols/IdSMTP.pas:L296–L299].

State outlives a call. `FCapabilities` is filled by `Connect` and read by `Authenticate`; `FDidAuthenticate` is set by `Authenticate` and checked by `Send` [Protocols/IdSMTP.pas:L270–L273]. There is no lock; the class assumes one thread per instance, as the rest of Indy does.

## Working in this file

Keep the three phases separable. Code that needs the capability list must run after `Connect` and before `Disconnect`; code that adds an authentication mechanism belongs in `Authenticate`'s `case`, not in `Connect`. When adding a mechanism, extend `TIdSMTPAuthenticationType` and the `case`, and decide explicitly whether the empty-username short-circuit applies to it.

## Risks and traps

- An empty `Username` makes `Authenticate` report success without contacting the server, so a configuration error surfaces as a relay refusal at `RCPT TO` rather than as an authentication failure [Protocols/IdSMTP.pas:L296–L299].
- `UseTLS = utUseRequireTLS` raises `EIdTLSClientSMTPNoSupport` when the server does not advertise `STARTTLS`, but only after the socket is open; the caller must still call `Disconnect` [Protocols/IdSMTP.pas:L238–L243].
- The `HELO` fallback discards the capability list, so a server that rejects `EHLO` is treated as offering neither TLS nor authentication, whatever it can do [Protocols/IdSMTP.pas:L230–L234].

## Routine map

- `TIdSMTP.Connect` — greeting, EHLO/HELO, optional STARTTLS — [Protocols/IdSMTP.pas:L212–L247]
- `TIdSMTP.Disconnect` — QUIT and close — [Protocols/IdSMTP.pas:L249–L266]
- `TIdSMTP.Send` — transmit a message after asserting the session state — [Protocols/IdSMTP.pas:L268–L281]
- `TIdSMTP.SendNoDate` — as `Send`, without restamping `Date` — [Protocols/IdSMTP.pas:L283–L290]
- `TIdSMTP.Authenticate` — mechanism selection and credential exchange — [Protocols/IdSMTP.pas:L292–L338]
- `TIdSMTP.SetUseEhlo` — property setter; refuses a change while connected — [Protocols/IdSMTP.pas:L340–L347]
