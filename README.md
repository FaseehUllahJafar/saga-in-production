# saga-in-production

A checkout saga in .NET 10, built for the failures that tutorials skip: a payment response lost after the charge went through, a timeout that doesn't tell you whether the step happened, a compensation that overtakes its own command, a node killed mid-saga, a saga that stops moving without an error.

Authorize payment → reserve stock → book shipment → capture payment. An orchestrator in Orders runs the steps. Wolverine handles the messaging over RabbitMQ, and each of the six services has its own SQL Server database. FakePay stands in for a card processor. Every failure named above has an integration test that runs against real containers and asserts on what ends up in each database.

This is the companion repo to my LinkedIn series on sagas. The posts simplify, and the repo doesn't. [Where they differ](#where-this-differs-from-the-posts) is listed below.

## Run it

You need Docker, the .NET 10 SDK and the Aspire CLI (`dotnet tool install -g Aspire.Cli`).

```
aspire run
```

That starts SQL Server, RabbitMQ and all six services, and prints the Aspire dashboard's link. Place an order (bash; on Windows, Git Bash):

```
curl -i -X POST http://localhost:5092/orders \
  -H "Content-Type: application/json" -H "Idempotency-Key: demo-1" \
  -d '{"customerEmail":"a@example.com","cardToken":"tok_visa","shippingAddress":"1 Main St","lines":[{"sku":"BOOK-DDD","quantity":1,"unitPrice":30}]}'
```

Then `GET /orders/{orderId}` shows the status and the step journal. In the dashboard, filter traces on `saga.id` to follow one order through every service and into FakePay.

To see the failures, change one field, and the `Idempotency-Key` too: a repeat of `demo-1` is the same order, however the body changed.

- `"cardToken": "tok_expired_auth"` fails at capture. The saga cancels the shipment, releases the stock and voids the authorization.
- `"shippingAddress": "UNDELIVERABLE"`: the carrier refuses the booking and everything before it is undone.
- `"cardToken": "tok_capture_down"`: every capture fails and leaves no trace. After its retries and an inquiry (about 8 minutes), the saga stops and waits for a human rather than guess whether the money moved.

The other tokens are listed at the top of [FakePayApi.cs](src/FakePay/FakePayApi.cs). `aspire run -- --monitoring` adds Prometheus, Alertmanager and Grafana.

Tests: `dotnet test` runs 30 unit tests, 44 integration tests (Testcontainers: SQL Server, RabbitMQ, Toxiproxy) and one end-to-end test through the real AppHost.

## What's worth reading

| | Where |
|---|---|
| **The payment response is lost after the charge commits.** Toxiproxy cuts FakePay's reply after the ledger write. The retry uses the same idempotency key, and there is exactly one authorization. This is the most important test in the repo. | [LostResponseTests.cs](tests/Saga.IntegrationTests/LostResponseTests.cs) |
| **A timeout means "I don't know", not "it failed".** The saga retries under the same CommandId, then asks the participant what happened, and only then compensates. A step that stays unknown is still compensated. | `Handle(StepTimeout)` in [CheckoutSaga.cs](src/Orders/Saga/CheckoutSaga.cs) |
| **Tombstones.** A void that arrives before its authorize writes a Cancelled row, so the late authorize is refused instead of leaving a live hold behind. | [TombstoneTests.cs](tests/Saga.IntegrationTests/TombstoneTests.cs), [ADR 0002](docs/adr/0002-idempotency-layers.md) |
| **Capture is the pivot.** Before it, every step has an invisible undo. A failed capture is retried forward unless the authorization itself is dead, and if the outcome is unknown the saga stops for a human instead of guessing. | [ADR 0005](docs/adr/0005-auth-capture-pivot.md) |
| **Monitoring for a saga that stops without erroring.** A gauge for the age of the oldest active saga, backed by a filtered index; dead-letter alerts that notice a new wave while old letters are still parked; and a runbook written after the alerts had fired on a live run. | [SagaMonitor.cs](src/Orders/Saga/SagaMonitor.cs), [runbook](docs/runbook.md) |

Also: [how the suite is tested](docs/testing.md), including the bugs it found; [who owns which retry](docs/adr/0003-retry-ownership.md); [a message changing shape while messages are in flight](docs/adr/0004-message-evolution.md); and two contrasts: the [same checkout choreographed](tests/Saga.IntegrationTests/Contrasts/Choreography/ChoreographyTests.cs), and the same checkout with [no saga at all](tests/Saga.IntegrationTests/Contrasts/NotASaga/NotASagaTests.cs), which is the right answer more often than a saga is.

## Where this differs from the posts

| Post | Said | The repo does | Why |
|---|---|---|---|
| Part 4 | Charge the card, and refund it if a later step fails | Authorize first, capture last, and **void** on failure | A void before capture is free and invisible to the customer. A refund is neither, and it isn't a compensation. It's a returns flow. [ADR 0005](docs/adr/0005-auth-capture-pivot.md) |
| Part 4 | Compensations must never fail | They can, and it's a state: `CompensationFailed`, with an alert and a runbook | A void can go unanswered for as long as the provider is down. Pretending it can't leaves no plan for when it happens. |
| Part 5 | Alert on dead-letter depth above zero | Two alerts: one pages when a dead letter has waited 10 minutes untriaged, and one opens a ticket when new ones arrive | While the first alert is already firing, a new wave of failures is invisible to it. The arrival alert is what shows that something new broke. |
| Part 5 | Azure Service Bus | RabbitMQ with quorum queues; dead letters in each service's own database | Free, and runs locally. One dead-letter sink per service, queried the same way everywhere. [ADR 0001](docs/adr/0001-plumbing-vs-logic.md) |
| Part 6 | Force a timeout and move the saga to Failed | A timeout triggers retries, then an inquiry. Compensation comes last, and it never assumes the step didn't happen | A timed-out payment may have been charged. Treating silence as failure is how a customer gets charged for an order that was cancelled. |

## Limits

This runs on a laptop, not in production. What production would still need:

- **Isolation.** Here it's one SQL Server container with six databases. In production each service would have its own server, credentials and backups.
- **Pre-generated handler code.** Wolverine compiles handlers at startup here. Production would pre-generate them (`codegen write`) and start in static mode.
- **Retention.** The idempotency tables, the saga table and the dead-letter archive grow forever. Nothing prunes them.
- **Auth on the operator endpoints.** `POST /admin/sagas/{id}/nudge` and `POST /admin/sagas/{id}/cancel` need no credentials.
- **A real provider.** FakePay copies Stripe's idempotency semantics but it is a simulator. The Shipping carrier stub keeps its bookings in memory, so a Shipping restart forgets them.
- **Expired authorizations.** A business that captures days later, on dispatch, would need re-authorization. Returns and refunds are out of scope.
- **Two alerts only proven offline.** The monitor-absence alerts are tested with `promtool` in CI but have not fired on a live run.
