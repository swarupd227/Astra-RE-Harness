# LLM Integration Architecture

**Status:** Kickoff (v0.1)
**Source:** Spec §6 (verbatim), with operational detail on adapters, prompt management, observability, and rollout.

---

## 1. Goals

The LLM is *not* the product — the signed spec is. The LLM is a high-leverage drafter whose output is always reviewed by a human. The integration layer is engineered for three things:

1. **Determinism of behavior, not output.** Same inputs → same provider, model, prompt template, parameters. Same configuration → same residency posture. The output text varies; the *contract under which it varies* must not.
2. **Auditability.** Every call carries enough metadata to reconstruct, months later, exactly what happened: provider, model, prompt template ID + version, residency config version, token counts, cost, latency, status, and (where retained) the request/response payloads.
3. **Safety.** Customer Fortran source is never retained by the provider. The system records the configuration version that proves this, on every call, in a column that auditors can query directly.

---

## 2. Provider abstraction

```csharp
public interface ILlmProvider
{
    string Name { get; }                            // "anthropic" | "azure_openai"
    LlmProviderConfigVersion ConfigVersion { get; } // ZDR/no-train snapshot identity
    IAsyncEnumerable<LlmResponseChunk> InvokeAsync(LlmRequest req, CancellationToken ct);
}

public sealed record LlmRequest(
    string Model,
    string PromptTemplateId,
    string PromptTemplateVersion,
    IReadOnlyList<LlmMessage> Messages,
    LlmParameters Parameters,        // temp, max_output_tokens, response_format, ...
    int MaxInputTokens,              // budget enforcement
    int MaxOutputTokens,
    decimal MaxCostUsd
);

public sealed record LlmResponseChunk(
    LlmChunkKind Kind,               // Token | Citation | StructuredField | Done | Error
    string? Text = null,
    string? ClaimPath = null,
    string? CitationLines = null,
    LlmFinishReason? Finish = null,
    LlmUsage? Usage = null
);
```

Two adapters in v1:

- **`AnthropicProvider`** — calls Anthropic's API via the dedicated zero-data-retention enterprise endpoint. Uses streaming responses. Models: `claude-sonnet-4-*` (extraction default).
- **`AzureOpenAIProvider`** — calls an Azure OpenAI deployment in a tenant configured for no abuse-monitoring, no human-review, no training. Uses streaming responses. Models: `gpt-4o-*` (scaffold default).

Both adapters implement the same chunked-stream interface. The caller (the worker) does not know which provider it is using — that is resolved by the `PromptRouting` table at request time.

### 2.1 Configuration version snapshot

Each adapter exposes a `ConfigVersion` value that captures, in a single string, the residency-relevant configuration:

```
anthropic:zdr-1:no-train:no-retention:endpoint=enterprise-eu-1
azure_openai:tenant=nous-prod-westeurope:abuse-mon=disabled:human-review=disabled:training=opt-out
```

This string is recorded on every `LlmCall` row (`provider_config_version`) and surfaced on the admin dashboard with the most recent provider audit-letter timestamp.

The string is computed at startup from the adapter's actual runtime configuration — *not* from a checked-in constant — so a misconfigured deployment cannot lie about its posture. A unit test (`Adapter_ConfigVersion_MatchesEnvironment`) gates CI.

---

## 3. Routing rules

Routing is data, not code. The `PromptRouting` table is keyed on `(stage, prompt_template_id)` and resolves to `(provider, model, parameters, fallback_provider, fallback_model)`.

```
stage    | prompt_template_id    | provider     | model               | fallback_provider | fallback_model
extract  | fortran-extract-v3.2  | anthropic    | claude-sonnet-4-... | azure_openai      | gpt-4o-...
scaffold | dotnet-scaffold-v2.0  | azure_openai | gpt-4o-...          | anthropic         | claude-sonnet-4-...
```

Admin can edit the row at runtime. The Engineer sees the resolved rule on the live extraction screen (provider context strip) — nothing about the routing is hidden.

**Failover semantics** (admin-toggleable per environment, per template):

- Trigger conditions: provider 5xx, rate-limit (429), or wall-clock timeout >120s.
- Behavior: the worker logs the primary failure to `LlmCall` (status `failure`, error code), then issues a *new* `LlmCall` against the fallback. Both rows are persisted.
- The user-visible state machine moves forward only if a successful call occurs; otherwise the subroutine returns to the prior state with a structured error.

For the demo (Appendix A), failover is **disabled** — a recorded backup is more controllable than a live failover. Admin re-enables after the demo.

---

## 4. Prompt templates

### 4.1 Storage & lifecycle

- Templates live under `src/api/Prompts/`, one Markdown file per `(template_id)/(version)`.
- Format: front-matter (target stage, inputs, output schema) + system + user sections, with `{{ variable }}` placeholders.
- Templates are **not** editable via the UI in v1. Changes go through code review like any other code.
- Loaded at deploy time into an in-memory registry. Hot-reload is not supported (deployment is the lifecycle event).

### 4.2 Phase-1 templates

| Template ID | Version | Stage | Default provider | Notes |
|---|---|---|---|---|
| `fortran-extract` | `v3.2` | Stage 3 | Anthropic Claude Sonnet 4 | Spec §6.2.1; structured-output where supported, schema-guided otherwise. |
| `dotnet-scaffold` | `v2.0` | Stage 5 | Azure OpenAI GPT-4o | Spec §6.2.2; structured response (one entry per output file). |

Versioning is `vMAJOR.MINOR`. MINOR for prompt-text refinements that preserve schema. MAJOR for schema-shape changes (require routing-table update).

### 4.3 Output validation

Both templates declare a JSON Schema for their output (`spec/v1.json` for extract, `scaffold/v1.json` for scaffold).

**Validation pipeline:**

1. Streaming JSON parser surfaces parse errors as soon as the response deviates from a JSON document shape.
2. On `done`, the full response is parsed and validated against the JSON Schema. Schema-invalid responses are *not* persisted as `Spec` / `Scaffold` rows; the raw response is stored on the `LlmCall` row in the restricted blob container, and the engineer is offered retry.
3. **Citation post-validation (extract only).** Every `lines` reference is verified against the actual source file. Unresolved citations become `warning` events (non-blocking — the SME resolves during review).

### 4.4 Token-budget enforcement

Each template declares `max_input_tokens`, `max_output_tokens`, `max_total_cost_per_call`. The worker:

- Counts input tokens (provider tokenizer where available; tiktoken-equivalent otherwise) before issuing the call.
- Rejects the call client-side with `error.code: extract.budget.input_exceeded` if input >`max_input_tokens`. The user sees: *"Subroutine source exceeds the prompt token budget. Try splitting at COMMON-block boundaries, or contact admin to raise the budget for this corpus."*
- Computes the upper-bound cost using `max_input_tokens + max_output_tokens` at the model's published rate. Rejects if >`max_total_cost_per_call`.

Per-template budgets are admin-editable.

---

## 5. Streaming pipeline

```
Worker ──▶ ILlmProvider.InvokeAsync ──▶ chunks ──▶ API channel ──▶ SSE consumer
                                          │
                                          ├──▶ append to in-memory buffer
                                          ├──▶ on `Token`: forward to SSE
                                          ├──▶ on `Citation`: forward to SSE + record on streaming-aggregate
                                          └──▶ on `Done`: validate, persist Spec/Scaffold + LlmCall, forward `done`
```

The API multiplexes one upstream provider stream to one SSE consumer. There is no broadcast — each extraction has at most one live consumer (the engineer's browser).

If the SSE connection drops mid-stream, the worker continues processing. On reconnect, the engineer's browser refreshes; if the call has already completed, the artifact is loaded from the database. If it is still in flight, a new SSE connection re-attaches via the persisted streaming buffer (Last-Event-ID handling).

---

## 6. Observability

Every LLM call produces an OpenTelemetry span with these attributes (spec §6.4, verbatim):

```
llm.provider                anthropic | azure_openai
llm.model                   claude-sonnet-4-... | gpt-4o-...
llm.prompt_template_id      fortran-extract
llm.prompt_template_version v3.2
llm.input_tokens            <int>
llm.output_tokens           <int>
llm.latency_ms              <int>
llm.cost_usd                <decimal>
llm.status                  success | failure | cancelled
llm.user_id                 <uuid>
llm.subroutine_id           <uuid>  (extract) | <null> (other)
```

**Dashboards** (Azure Monitor, live by end of Phase B):

- *LLM cost & latency*: cost per day per provider per stage; p50/p95/p99 latency per template; error-rate trend.
- *Pipeline throughput*: subroutines per state, average time-in-state per stage.
- *Operational health*: dependency healthchecks, queue depths, worker-pool saturation.

**Alerts** (PagerDuty):

- `error_rate > 5%` over a 15-minute window → on-call page.
- `p99_latency > 60s` over a 15-minute window → warn.
- `daily_cost > 120% of 7-day average` → warn.
- `daily_cost > hard_cap` → page + automatic LLM-call rejection until admin clears.

---

## 7. Cost controls

Three layers of defense:

1. **Per-call budget.** Template-declared `max_total_cost_per_call`. Pre-flight check.
2. **Per-corpus daily quota.** `200 calls / corpus / day` default; admin override per corpus.
3. **Per-environment daily hard cap.** A USD ceiling per environment per day. Exceeding rejects all LLM calls until admin clears (an audited action).

Cost rollup runs hourly. Engineers see per-corpus cost in the corpus detail view; admin sees per-engineer + per-stage in the cost dashboard.

---

## 8. Source-residency proof

The single most consequential gating requirement (spec §6.3). Three reinforcements:

1. **Adapter-level config snapshot** (§2.1) — recorded on every call.
2. **Admin dashboard** — surfaces the active `provider_config_version` and the most recent provider audit-letter timestamp. If the audit-letter is >12 months old, the row turns amber; >18 months, red.
3. **CI gate** — a deployment-time test fetches the running adapter's `ConfigVersion`, compares to a per-environment expected value committed in the deploy manifest, and fails the pipeline on mismatch. This makes a residency drift undetectable-only after a deliberate manifest edit.

---

## 9. Failure handling (user-facing copy)

| Failure mode | User message | Backend action |
|---|---|---|
| Provider rate-limited (429) | "Provider returned a rate-limit error. Retry in {retry-after} seconds." | Job marked retryable; engineer can retry; auto-retry once after `retry-after`. |
| Provider 5xx | "Provider returned a server error — the call did not complete. Retry, or switch to fallback provider." | Job marked retryable; admin can flip routing to fallback per corpus. |
| Malformed JSON in response | "Response did not match expected schema. View raw response or retry." | Spec/Scaffold not persisted; raw response stored on `LlmCall.restricted_response_blob_uri`; retry available. |
| Token-budget exceeded (pre-flight) | "Subroutine source exceeds the prompt token budget. Try splitting at COMMON-block boundaries, or contact admin to raise the budget for this corpus." | No call made; subroutine remains in PARSED. |
| Network/SSE drop | "Connection dropped mid-extraction. The call may still have completed — refresh to check status." | Backend continues; spec persists if backend completes; reload checks final state. |
| Hard-cap hit | "LLM calls are temporarily disabled for this environment (daily cost cap hit). Contact admin." | All new calls rejected; admin notified; existing in-flight calls allowed to complete. |

Copy is committed in `frontend/src/copy/llm-errors.ts` so engineering and design can iterate on it.

---

## 10. Rollout & template iteration

Phase B ships `fortran-extract-v3.2` and `dotnet-scaffold-v2.0` as the demo templates. Phase C iterates:

- Per-corpus telemetry on accept-without-edit rate per template version.
- Weekly prompt-iteration cycle during Phase C: introduce `v3.3`, dual-route a fraction of calls (admin-set), compare accept rates, promote when ≥70% target is sustained.
- Templates older than two MAJOR versions are removed from routing options to keep the matrix small.

---

## 11. Out of scope (v1)

- UI editing of prompt templates.
- Multiple concurrent prompt experiments per template (A/B/C). The dual-route capability supports two-way A/B only.
- Customer-facing LLM playground.
- LLM fine-tuning. The product is contractually no-fine-tuning.
- LLM-driven actions outside the two stages (e.g., LLM commenting on a spec) — out of scope for v1.
