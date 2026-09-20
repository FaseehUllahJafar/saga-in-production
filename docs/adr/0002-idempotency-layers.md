# 0002: Idempotency in three layers, each covering a different duplicate

Status: accepted

## Context

A saga retries on purpose. A timeout re-sends the command, the broker redelivers after a crash, and a client double-clicks checkout. Each of these produces a duplicate that looks different on the wire. No single dedupe mechanism sees all of them.

## Decision

| Layer | Where | Key | Catches |
|---|---|---|---|
| Client | `POST /orders` | `Idempotency-Key` header, scoped to the customer | A retried or double-clicked checkout. The order id is derived from the key, and `CheckoutSaga.StartOrHandle` absorbs a repeat that overtakes the first. |
| Transport | Wolverine durable inbox | MessageId | Broker redelivery of the *same* message after a crash or a lost ack |
| Business | Each participant's `(SagaId, Step)` row, plus FakePay's idempotency keys | CommandId | Saga retries. They are *new* messages (new MessageId) carrying the same CommandId, so the inbox lets them through by design. |

- **CommandId is derived, not stored.** It is SHA-256 of `{SagaId}:{step}:{fwd|comp}`. A restarted orchestrator re-derives the same id, and it doubles as the idempotency key at FakePay.
- **A duplicate gets the stored outcome.** It is an idempotent success: the same reply, never an exception, never a dead letter. That is what lets the saga match replies on CommandId and ignore attempt numbers.
- **Tombstones.** A compensation that arrives before its forward command writes the step row as Cancelled, so the late forward command is rejected. Compensating something that never happened is a successful no-op.
- **A late effect cleans itself up.** An effect can land after its step was cancelled: the authorize was still inside FakePay when the void ran. The forward handler sees the tombstone and releases what landed. That cleanup is the one place a participant throws on an unreachable dependency, because nobody else will ever come back for it (`DependencyUnavailableException`, retried by the transport).
- **The settle window.** "FakePay has no record" is only conclusive once FakePay can no longer be processing our request. A void that finds nothing within `SettleWindow` of the last provider call waits and asks again (`OutcomeNotSettledException`). The integration test `VoidOvertakesInFlightAuthorize_BeforeCommit_ChargeLandsAndIsVoided` found this gap.

## Consequences

- Every participant has one small table whose primary key is the business idempotency key. These tables grow with order volume and need a retention job, which is not built here (see Limits).
- FakePay idempotency keys stop being honoured for *replay* after 24 hours, as at Stripe. The lookup endpoint ignores that expiry, because a void must still find an authorization for as long as the authorization can be live.
