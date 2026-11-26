# Parallel agent runs

Many per-routine agents at once, stopping where a person is needed. Nothing here is specific to a source language,
target stack or client.

## Say it

| Sentence | Tool | Who |
|---|---|---|
| "Run wave 1" · "Run the next wave" · "Execute all the waves with a budget of $20" | `run_wave` | engineer |
| "Extract the specs for all routines" · "Draft specs for 40 routines" | `extract_many` | engineer, admin |
| "Generate code for all signed routines" | `generate_many` | engineer |
| "Run the compile gate for all routines" | `run_gates_many` | engineer |
| "How is the wave run going?" | `get_work_run` | anyone |
| "Show the review queue" | `get_review_queue` | anyone |
| "Pause / resume / cancel the wave run" · "Retry the failed steps of the wave run" | `control_work_run` | engineer, admin |

Without a number, a batch takes every matching routine; one run holds at most 500 and says "500 of N" when there
are more.

Each batch pauses on **one** Confirm card that states how many of what will run, how wide, where it waits for a
person, and the cost ceiling. REST equivalents: `Endpoints/WorkEndpoints.cs`.

A wave comes from the programme's **approved** migration plan; a draft is not executed.

## The queue

`work_runs` and `work_items` in Postgres. Each routine becomes a chain
`extract → signoff → scaffold → compile → test-pack`, entered at the stage the routine's state calls for, leaves
(lower waves) first.

- **Leasing**: `FOR UPDATE SKIP LOCKED` under a per-stage advisory lock, so caps hold with several API instances.
  The owner heartbeats; finishing writes are fenced on the lease owner.
- **Restart**: a graceful stop hands leases back; a killed process leaves them to lapse (`Work:LeaseSeconds`, 90)
  and the next scheduler reclaims them. Every step recognises work that already finished and does not pay twice.
- **`signoff`** is a wait state. No worker leases it; the scheduler completes it only when it observes the
  routine's spec signed. Drafts a run waits on are routed for review as the requesting engineer. No tool in this
  path reviews a claim, signs, approves or commits.
- **Failures**: provider-type failures retry with back-off (3 attempts); a failed gate does not. Later steps of
  that routine are `SKIPPED`; "retry the failed steps" queues them again.
- **Pause** stops new leases (running steps finish). **Cancel** keeps finished work and closes the rest.

## The governor

| Limit | Setting |
|---|---|
| Agents per stage, per run | run caps (default extract 6, scaffold 4, compile 2, test-pack 2; "… 3 at a time") |
| Agents per stage, whole system | `Work:Caps:<stage>` (default twice the per-run figure) |
| Interactive before batch: slots batch never takes | `Llm:Anthropic:RateLimit:InteractiveReserve` (4) |
| Batch tokens per rolling minute | `Llm:Anthropic:RateLimit:BatchTokensPerMinute` (0 = off) |
| Cost ceiling per run | `budgetUsd`; the next model call is refused, the run pauses with the reason |
| Concurrent builds in the API container | `Validation:MaxConcurrentGates` (3), shared by every way of starting a gate |
| Execute queued work on this instance | `Work:Enabled` (true) |

A cost ceiling is checked before each call, so a run can overshoot by the calls already in flight.

## What is recorded

Per item: attempts, start and end, calls, tokens, cost. Per run, on completion (`metrics_json`): elapsed time,
**serial** agent time (sum of the machine steps), **busy** time (during which at least one agent worked), their
ratio, and peak concurrency. Time waiting for a signature is in neither agent figure. The ratio compares against
the same steps run one after another; it is not a separate serial run.

## Not covered

- Batches run the compile and test-pack gates. The quality gate and the fix-until-green loop still take one routine
  at a time.
- The gate pool and the limiter are per process; only the queue's caps are shared between instances.
- `RunEventBus` and the Narrator stay in memory: after a restart they are rebuilt from the rows, and the Narrator
  may repeat its latest "waiting for sign-off" note.
