# How this repo is tested

There are three layers. Each one answers a different question.

| Layer | Project | Runs against | Question it answers |
|---|---|---|---|
| Unit | `tests/Orders.UnitTests` | `CheckoutSaga` objects, a `FakeTimeProvider` | Is the transition table right? Covers gating, the journal, the pivot and cached replies. |
| Integration | `tests/Saga.IntegrationTests` | Real SQL Server, RabbitMQ and Toxiproxy containers. Every service runs in-process from its production `Add*Service()`. | Does the system survive the failure, judged by what ends up in each service's database? |
| End to end | `tests/Saga.E2E` | The real AppHost via `Aspire.Hosting.Testing` | Does `aspire run` plus the HTTP API work? |

## Integration suite: rules

- **Outcomes, not traces.** Every assertion reads a service's own tables: saga status and journal, FakePay's ledger, stock, reservations, shipments, and envelope and dead-letter rows. Nothing asserts on logs.
- **No fixed sleeps as synchronisation.** Tests poll against a deadline. The one deliberate delay, in the lost-response test, lets a real client timeout elapse.
- **Isolation by id.** Each test seeds its own SKUs and uses its own SagaIds. Envelope and dead-letter queries filter by the saga id inside the message body.
- **Quiet between tests.** Before each test, and after each saga finishes, the harness waits until no service has incoming, outgoing or due-soon scheduled envelopes and every RabbitMQ queue is empty.
- **Deterministic races.** Timing-dependent scenarios are forced, not hoped for:
  - FakePay's `tok_slow` and `tok_slow_commit` hold a request after or before its commit.
  - Toxiproxy drops responses while letting requests through.
  - A test-only EF interceptor holds a saga commit open, so a reply and a timeout really collide. The test asserts that the conflict happened.

## What each failure test reproduces

| Test | Production failure |
|---|---|
| `FakePayResponseLost_RetrySameKey_ExactlyOneAuthorization` | The charge commits and the response dies on the wire. The retry must not charge twice. |
| `AuthorizeTimesOut_ChargeLanded_InquiryFindsIt_ContinuesForward` | Every answer is late. The inquiry finds the charge and the sale goes ahead. |
| `InquiryUnanswered_CompensationStillFindsChargeAndVoidsIt` | No answer at all. The step stays Unknown, is still compensated, and the void finds the charge by key. |
| `VoidOvertakesInFlightAuthorize_BeforeCommit_ChargeLandsAndIsVoided` | FakePay is slower than our client timeout. The void must not conclude "nothing to void" too early (this is the settle window). |
| `VoidOvertakesInFlightAuthorize_AfterCommit_VoidFindsChargeByKey` | The same race on the other side of the commit. |
| `VoidBeforeAuthorize_LateAuthorizeRejected_NothingInTheLedger`, `ReleaseBeforeReserve_NoLeakedStock` | A compensation overtakes its command (tombstones). |
| `RetryAfterTimeout_NewMessageId_SameCommandId_AuthorizesOnce` | Three concurrent copies of a retried command. |
| `DuplicateReserve_ReturnsStoredOutcome_StockTakenOnce` | Redelivery after the saga moved on. |
| `BrokerDownDuringCommit_StatePersists_MessageDrainsLater` | RabbitMQ is unreachable at commit time (the outbox). |
| `OrchestratorRestart_ScheduledTimeoutSurvives_SagaCompletes` | Orders restarts while a participant swallowed the command. Only the persisted timeout can move the saga on. |
| `PaymentsStoppedMidProviderCall_RedeliveredOnRestart_ExactlyOneAuthorization` | A participant stops inside a provider call. |
| `ReplyRacesTimeout_RowVersionConflict_LoserReEvaluates_SagaCompletesOnce` | A reply and a timeout for the same step at the same moment. |
| `LastUnit_TenConcurrentOrders_ExactlyOneCompletes_StockNeverNegative` | A flash sale on the last unit. |
| `CaptureTransientFailure_RetriesForward_Completes` | A processor blip at the pivot. |
| `CaptureTerminalFailure_CancelsReleasesVoids` | The authorization expired before capture. |
| `CaptureOutcomeUnknown_StopsForManualReview_ShipmentKept` | Silence past the pivot: the capture landed, but no answer came back. |
| `CaptureInquiryFindsNothing_StopsForManualReview_AuthorizationKept` | The inquiry past the pivot says "no trace". That is still not safe to act on backwards. |
| `CompensationExhausted_EndsCompensationFailed` | Payments is down for longer than the compensation budget. |
| `InsufficientStock_VoidsAuthorization_NothingDeadLettered` | A business rejection is a reply, not an exception. |
| `InquiryReachesInventory_AndNoStockIsLeakedEitherWay` | The inquiry reaches a non-payment participant. It can overtake a queued reserve, and either ending must leave stock exact. |
| `PoisonMessage_LandsInDeadLettersOnce` | A malformed command. |
| `EmailProviderDown_SagaStillCompletes_NotificationRetriedUntilSent` | A side channel is down. It must not hold the saga hostage. |

## Known gaps

- **Graceful stop, not a kill.** The restart tests stop hosts gracefully, so Wolverine releases the node's envelopes. Recovery after a hard kill (node reassignment by the durability agent) is not exercised.
- **Shipping's carrier stub keeps its ledger in memory**, so no test restarts Shipping mid-saga.

## How the suite was designed

The suite went through a review council before and after it was written: Opus 5.5, Sonnet 5, Sonnet 4.6, Sonnet 4.5 and Gemini 3.1 Pro. Findings were accepted only after checking them against the code, and several were rejected with reasons. Between the council and the suite's own first runs, these production bugs were found, fixed and pinned by a test:

- **Decimal scale broke retries.** A retry built from a reloaded saga sent `25.00`, where the original request had sent `25`. FakePay saw a different request under the same key and refused it. Amounts now travel as integer minor units. Found by the lost-response test.
- **Losers of the reply-versus-timeout race were dead-lettered.** Wolverine raises `SagaConcurrencyException`, which the retry policy did not cover. Found by the rowversion race test.
- **A void concluded "nothing to void" while FakePay was still working on the authorize.** This is fixed by the settle window. Found by writing the in-flight race test.
- **A NotFound inquiry at capture compensated a possibly-paid order.** Found by three council members independently.
- **Leaked authorizations and bookings.** They leaked when cleanup of a late effect failed.
- **Handler discovery broke with several hosts in one process.**
