# Runbook

One section per alert in [monitoring/alert-rules.yml](../monitoring/alert-rules.yml), plus the procedures they point to. I wrote it after the alerts had fired for real, on a local run with `aspire run -- --monitoring`, and each section says what that run showed.

This is a demo with no on-call rotation. "Owner" says who would own the alert in a real team.

## Before you start

- **Dashboards.** Grafana's "Saga overview" answers "is anything wrong?". The Aspire dashboard answers "what happened to this order?": filter traces or structured logs on `saga.id` (or the `SagaId` log attribute). The order id the API returns *is* the saga id. FakePay's side of each call carries the same tag.
- **SQL.** Each service owns its own database (`orders-db`, `payments-db`, `inventory-db`, `shipping-db`, `notifications-db`, `fakepay-db`). The saga lives in `orders.CheckoutSagas`, and Wolverine's tables live in the `wolverine` schema of every service database.
- **Always run `sqlcmd` with `-I -b`.** `-b` stops a script at the first error instead of running the rest. `-I` sets QUOTED_IDENTIFIER ON. The saga table and Wolverine's envelope tables have filtered indexes, and without `-I` every `UPDATE` or `DELETE` fails with error 1934. It did the first time.
- **Never publish into a service queue by hand** from the RabbitMQ UI. See [Traps](#traps).

## SagaStuck

**Fires when** the least recently updated `InProgress`/`Compensating` saga has not moved for 15 minutes (`saga_oldest_active_age_seconds > 900`, for 2 minutes). Severity: page.

**Means** the message this saga is waiting for is gone. Every waiting state has its own timeout, and each attempt updates `LastUpdatedUtc`; the longest single wait is 8 minutes. So a saga this quiet is not slow, it is orphaned: its scheduled timeout was deleted, its command was purged from a queue, or its reply was dead-lettered and then discarded.

**First run:** a saga frozen at `reserve-stock` attempt 1. Its scheduled timeout was deleted and the inventory queue purged, as an operator "clearing a stuck queue" would. The gauge climbed steadily. The alert went pending at 15 minutes and fired 2 minutes later.

**Look**

1. The Orders log names every stuck saga on each poll: `Saga {SagaId} stuck: {Status} at {Step} attempt {Attempt}, no progress since ...`. Or run the monitor's own query:
   ```sql
   -- orders-db
   SELECT TOP (20) Id, Status, CurrentStep, AttemptCount, LastUpdatedUtc
   FROM orders.CheckoutSagas
   WHERE Status IN ('InProgress', 'Compensating') AND LastUpdatedUtc < DATEADD(minute, -15, SYSDATETIMEOFFSET())
   ORDER BY LastUpdatedUtc;
   ```
2. Is anything still scheduled for it? An empty result is the lost-timeout case.
   ```sql
   -- orders-db
   SELECT id, status, execution_time, owner_id FROM wolverine.wolverine_incoming_envelopes
   WHERE message_type = 'Orders.Saga.StepTimeout' AND CAST(body AS varchar(max)) LIKE '%<saga id>%';
   ```
3. Is its command or reply in a dead-letter table? Check the owning participant (`CurrentStep`: payment steps → `payments-db`, `reserve-stock` → `inventory-db`, `book-shipment` → `shipping-db`) and `orders-db`. If it is, go to [DeadLetterUntriaged](#deadletteruntriaged) first.
4. Is the participant up, and its queue moving? Check the Aspire dashboard and the RabbitMQ queue depth panel.

**Fix:** once the cause is gone (participant back, poison message handled), nudge the saga:
```
POST /admin/sagas/<saga id>/nudge
```
This sends the saga the very timeout it lost, for its current step, attempt and direction. The saga then does what it would have done: re-sends the command under the same CommandId (participants dedupe on it, so nothing happens twice), or moves on to the inquiry. It is safe to repeat. A nudge that races the real timeout, or arrives after the saga has moved, is discarded as stale by the saga's own gating. The response is 409 for a saga that isn't in flight. Tested by `FrozenSaga_TimeoutDeletedAndCommandPurged_NudgeResumesIt`.

**First run:** the Orders log named the saga (`stuck: InProgress at reserve-stock attempt 1`), and the scheduled-timeout query came back empty. The nudge returned 202, and the saga completed within seconds. A second nudge got 409. The age gauge was back to 0 on the next poll and the alert resolved.

**Escalate** if a nudged saga goes quiet again at the same step. That is a bug in the participant, not a lost message. **Owner:** the team that owns Orders.

## SagaNeedsAttention

**Fires when** any saga sits in `CompensationFailed` or `NeedsManualReview` (`saga_needs_attention > 0`, for 1 minute). It keeps firing until the row leaves that state. Severity: page.

**Means** the saga has done everything it can safely do on its own, and a person owes it a decision:

- `NeedsManualReview`: past the pivot, the capture outcome is unknown. Going backwards could cancel a paid order; going forwards could charge twice. The saga stops rather than guess.
- `CompensationFailed`: an undo (void, release, cancel) went unanswered through its whole retry budget, about 15 minutes. A real effect may still exist: a live authorization, held stock, a booked shipment.

**First run:** a `tok_capture_down` order. Every capture got a 503, the three attempts took 1+2+4 minutes, and then the inquiry found no trace of the capture. The saga parked in `NeedsManualReview` and the alert fired a minute later.

**Look**

```sql
-- orders-db: why, and how far it got
SELECT Id, Status, CurrentStep, FailureReason, AuthorizationId, TrackingNumber, Journal
FROM orders.CheckoutSagas WHERE Status IN ('NeedsManualReview', 'CompensationFailed');

-- payments-db: each payment step, with the CommandId it was sent under
SELECT Step, CommandId, Status, ProviderRef, UpdatedUtc FROM payments.Steps WHERE SagaId = '<saga id>';
```
Then ask the provider what happened, by the idempotency key (the CommandId):
```
GET <fakepay>/v1/lookup/capture/<capture CommandId>
GET <fakepay>/v1/lookup/authorize/<authorize CommandId>
```
On the first run, the capture lookup returned `404 no_such_request` and the authorization came back `authorized`, `captureId: null`. The money had not moved and the hold was still live.

**Decide** (in a real team, with whoever owns payments operations):

| Provider says | State of the world | Resolution |
|---|---|---|
| Capture exists | Paid, shipment booked | Treat the order as completed; the customer email didn't go out. |
| No capture, authorization live | Nothing charged, stock held, shipment booked | Either capture at the provider and treat the order as completed, or cancel: void at the provider, cancel the shipment, release the stock. |
| Authorization expired or voided | Nothing charged, and nothing to capture | Cancel: cancel the shipment, release the stock. |

For `CompensationFailed`, finish the undo the saga could not: the journal shows which step, and that step's participant table shows its state.

**Record** the decision on the saga, which clears the alert:
```sql
-- orders-db (sqlcmd -I)
UPDATE orders.CheckoutSagas
SET Status = 'Cancelled',            -- or 'Completed'
    FailureReason = CONCAT(FailureReason, ' | resolved by <who> <when>: <what was done>'),
    LastUpdatedUtc = SYSDATETIMEOFFSET()
WHERE Id = '<saga id>' AND Status IN ('NeedsManualReview', 'CompensationFailed');
```
This records what a person did. It does not do it: no messages are sent, so no events go out and no notification either. That is on purpose. Anything that moves money or stock is done at its source first, and this row is updated afterwards.

**First run, resolved** as "cancel": the authorization was voided at FakePay (`POST /v1/authorizations/<id>/void`) and the decision recorded with the `UPDATE` above. The alert cleared on the next poll after Orders came back. The demo has no operator API for Shipping or Inventory, so the booked shipment and the held unit were left in place. A real system needs those levers before it needs this runbook.

**Escalate** at once if the provider says "captured" and the shipment was cancelled: a paid order was cancelled. **Owner:** payments operations, with the Orders team.

## SagaCompensationRateHigh

**Fires when** the share of sagas that compensated in the last 15 minutes is over 3× the day before's share, and over 10% (the floor stands in for "usual" until there is a day of history), with at least 20 sagas started. For 5 minutes. Severity: ticket.

**Means** something upstream is saying no more often than usual: a card processor declining, stock running out, a carrier rejecting. The saga is doing its job; the business is losing orders.

**First run:** 22 declined cards against 6 good orders. This run also found a bug in the alert. Every counter series was born at its first increment, so `increase()` had no earlier sample to measure from, and 22 compensations in one export interval read as about 18%. The counters now start at 0 when the service starts (`SagaMetrics`).

**Look:** the Grafana panel "Started, completed and compensated". `saga_compensations_total` is labelled by the step that failed, so `sum by (step) (increase(saga_compensations_total[15m]))` says which participant is turning orders away. Its log lines say why (`compensating after {Step}: {Reason}`).

**Escalate** to the team that owns that participant or its dependency. **Owner:** the Orders team triages; the step's owner fixes.

## DeadLetterUntriaged

**Fires when** a service has had any dead letter for 10 minutes (`dead_letters > 0`, for 10 minutes). It keeps firing until they are replayed or deleted. Severity: page.

**Means** a message failed in a way the retry policy treats as a bug (ADR 0003), and nobody has looked at it yet. Dead letters are the one place a message can stop without anything else noticing. A dead-lettered `StartCheckout` is an order the API accepted with a 202 that has no saga at all, so SagaStuck will never see it (`DeadLetteredStart_OnlyTheDeadLetterMonitorSeesIt_RunbookReplayCompletesTheSaga`).

The alert is on presence, not on the `dead_letter_oldest_age_seconds` gauge. A message that arrived without Wolverine's headers is stored with `sent_at = 0001-01-01` and has no usable age. The first run caught the gauge reporting a malformed message as 2,000 years old; it now ignores such dates.

**Look**
```sql
-- the service's database, e.g. inventory-db
SELECT id, message_type, exception_type, exception_message, sent_at, received_at
FROM wolverine.wolverine_dead_letters ORDER BY sent_at;
```
`exception_type` sorts them into two kinds:

- **The message is fine and the handler failed** (a bug since fixed, a dependency that was down past its retries). → [Replay](#replay-dead-letters).
- **The message itself is bad** (`JsonException`, a null where a value is required). Replaying it just fails again. → [Delete](#delete-dead-letters), after keeping a copy.

**First run:** a hand-published `ReserveStock` with a truncated JSON body landed in `inventory-db` with `System.Text.Json.JsonException`. This alert fired 10 minutes later. The message was archived and deleted, and the alert resolved. (That run's procedure used `SELECT INTO` a dated table, without a transaction. A review found that a second delete the same day would fail the copy and still delete the message; the procedure below is the fix.)

**Owner:** the team that owns the service whose table it is.

## DeadLettersArriving

**Fires when** a service has more dead letters than it had 5 minutes ago. Severity: ticket.

**Means** it is happening now. A burst usually has one cause (a deploy, a bad producer), so look at the newest rows' `exception_type` together. The alert does not use Wolverine's own `wolverine_dead_letter_queue_Messages_total` counter. That series is born with the value 1 at a service's first dead letter, so `increase()` could never see the first one, and on the first run it didn't.

Handle as [DeadLetterUntriaged](#deadletteruntriaged), sooner.

## MonitorSilent

**Fires when** a monitor's heartbeat (`saga_monitor_last_run_timestamp`, labelled `monitor="sagas"` or `"dead_letters"`) has been more than 3 minutes old for a minute. It keeps firing for an hour after a replica stops reporting entirely. `MonitorAbsent` covers "no saga monitor has reported for 10 minutes", and `MonitorNeverReported` covers a service that is up and sending metrics but whose dead-letter monitor has not reported in 10 minutes. A monitor that fails on every poll emits no heartbeat series at all, so MonitorSilent has nothing to measure. Severity: page.

**Means** the numbers the other alerts read are stale. A monitor that can't reach its database keeps its last values, and those may say "all quiet". Treat every saga and dead-letter alert as unknown until this clears.

**First run:** stopping `orders` fired it for both of its monitors 3 minutes later. It had no `for:` then; with the minute added since, expect about 4. `MonitorAbsent` also paged once when Prometheus started, before any service had reported; that is why it now has `for: 5m`.

**Look:** is the service up (Aspire dashboard)? If it is, its log has `Saga monitor poll failed` or `Dead-letter monitor poll failed` with the SQL error. The usual cause is its database.

**Owner:** whoever owns the service; the platform team if it is Prometheus or the network.

## Procedures

### Replay dead letters

Only after the cause is fixed: a replay into the same bug just dead-letters again.

```sql
-- the service's database (sqlcmd -I). One message:
UPDATE wolverine.wolverine_dead_letters SET replayable = 1 WHERE id = '<id>';
-- or everything of one type that failed the same way:
UPDATE wolverine.wolverine_dead_letters SET replayable = 1
WHERE message_type = '<type>' AND exception_type = '<exception type>';
```
Wolverine's durability agent moves replayable rows back into the inbox within seconds, and the service handles them as if they had just arrived. Replaying a reply or command that the saga has since moved past is harmless: the saga discards it as stale, and participants answer from their idempotency table. Tested verbatim by `DeadLetteredStart_OnlyTheDeadLetterMonitorSeesIt_RunbookReplayCompletesTheSaga`.

### Delete dead letters

Keep the evidence, then delete, in one transaction, so a failed copy can't be followed by a delete:
```sql
-- the service's database (sqlcmd -I -b)
IF OBJECT_ID('wolverine.dead_letters_archive') IS NULL
    SELECT TOP (0) * INTO wolverine.dead_letters_archive FROM wolverine.wolverine_dead_letters;

SET XACT_ABORT ON;
BEGIN TRANSACTION;
INSERT INTO wolverine.dead_letters_archive SELECT * FROM wolverine.wolverine_dead_letters WHERE id = '<id>';
DELETE FROM wolverine.wolverine_dead_letters WHERE id = '<id>';
COMMIT;
```
If the bad message belonged to a saga, that saga is now missing a command or reply: check it with [SagaStuck](#sagastuck)'s queries and nudge it.

### A crashed node

A killed Orders process (OOM, lost host) leaves its outbox rows and its scheduled timeouts owned by a node that no longer exists. A surviving node takes them over once the dead node's heartbeat is older than Wolverine's `StaleNodeTimeout` (60 seconds by default). Until then, that node's sagas are frozen. That is well inside SagaStuck's 15 minutes, so a crash on its own should never page. If it does, the survivors aren't running, or they can't reach the database. Tested by `OrdersNodeKilled_WithCommandsInItsOutbox_SurvivorTakesThemOver_SagaCompletes`.

## Traps

- **A message without Wolverine's headers is not dead-lettered, it is discarded.** On Wolverine 6.40, a message that arrives on a durable listener with no message type (for example, published by hand from the RabbitMQ management UI without the AMQP `type` property) is logged as "Moving to dead letter queue". Dead-lettering it then throws a `NullReferenceException` in `ErrorReport`, and after 3 attempts the envelope is dropped (`Discarding message ... after 3 attempts`). No row, no metric, no alert: only that error log. Seen on the first run. So never inject messages by hand; to resend one, replay its dead letter.
- **`sqlcmd` without `-I`** fails every write to these tables with error 1934 (see [Before you start](#before-you-start)).
- **A service's first 5 minutes.** `DeadLettersArriving` compares with 5 minutes ago. Right after a restart there is no earlier sample, so it compares with 0: dead letters already parked at startup fire it once.
