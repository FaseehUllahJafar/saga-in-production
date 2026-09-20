# 0003: Who owns which retry

Status: accepted

## Context

Retries stack. Suppose the HTTP client retries 3 times, the transport retries each message 3 times, and the saga retries each step 3 times. One provider outage then turns into 27 calls per order, arriving just as the provider is trying to recover. Every layer also has to be idempotent against every other. Each retry needs exactly one owner.

## Decision

**The saga owns business retries.** A step that gets no answer is retried by the saga's own `StepTimeout`, with growing timeouts (1, 2, then 4 minutes in production). When those run out, the saga asks the participant what happened (`CheckStepStatus`) and acts on the answer. Participants that can't reach their dependency **do not throw and do not reply**. The silence is the signal, and the saga's timer decides what happens next. `InsufficientStock`, `PaymentDeclined` and similar answers are business replies, never exceptions.

**The transport owns infrastructure blips only.** These are the policies in `ServiceDefaults/SagaMessaging.cs`:

| Failure | Policy | Why |
|---|---|---|
| `DbUpdateConcurrencyException` (saga rowversion) | Scheduled retry: 50 ms to 2 s, 5 tries | Re-run from the queue so the handler reloads fresh state. Never retry in place with a stale tracked entity. |
| Unique violation on a `(SagaId, Step)` key | Scheduled retry, 3 tries | The loser re-runs, finds the winner's row and replays its outcome. |
| Transient SQL error (including ones EF wraps in `DbUpdateException`) | Retry with cooldown, 3 tries | A deadlock victim or a dropped connection. |
| `DependencyUnavailableException` | Scheduled retry: 1 s to 10 min | Only for releasing a late effect, which nobody else will ever retry (see ADR 0002). |
| `OutcomeNotSettledException` | Scheduled retry: 1 s to 30 s | The settle window resolves by itself, and the saga is waiting for the answer. |
| Anything else | Dead-letter at once | A bug or a poison message. Retrying won't fix it. |

**HTTP clients have no retry handler.** `FakePayClient` makes one call with a timeout.

**Notifications sit outside the saga** and have their own patient policy (5 s, 30 s, 2 min, 10 min, then dead-letter). An email outage is worth waiting out, and it must never reach back into the checkout.

## Consequences

- Retry counts are knowable. At most 3 forward attempts per step, plus one inquiry, reach a participant.
- Wolverine picks the retry slot from the envelope's total failure count across all exception types. A message that has already failed on a concurrency conflict starts the next schedule one slot later. We accept that; the schedules are long enough to absorb it.
- There is one dead-letter sink (`wolverine.wolverine_dead_letters` in each service's database), and anything in it is worth a human's attention. Recovery is manual, via the runbook.
