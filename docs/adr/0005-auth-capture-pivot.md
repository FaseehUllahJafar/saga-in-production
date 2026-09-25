# 0005: Authorize first, capture last, and capture is the pivot

Status: accepted

## Context

Part 4 of the series used the textbook example: charge the card, reserve stock, book the shipment, and if shipping fails, refund the charge. That order is wrong for card payments, and the "refund" in it is not a compensation.

- **A refund is visible and it costs money.** The customer sees a charge and then a credit, days apart. The processor keeps its fee. Some cards and schemes won't refund to the original method at all. A compensation that the customer can see, and that loses money, is a second business event, not an undo.
- **Cards already separate the hold from the money.** An authorization places a hold. A capture moves the money. A void releases the hold before capture: the customer never sees a charge and there is no fee. That is the undo the saga needs, and every processor offers it.
- **Every saga has a step after which it can't go back.** If you don't choose that step, the step order chooses it for you, usually badly.

## Decision

The order is **Authorize → Reserve stock → Book shipment → Capture**.

- Every step before capture has a real, invisible compensation: **void** the authorization, **release** the stock, **cancel** the booking.
- **Capture is the pivot.** It goes last because it is the one step whose effect can't be undone quietly. Everything that could still fail for a business reason (card declined, out of stock, address not serviceable) has already been decided before any money moves.
- **Past the pivot the saga only goes forward.** A transient capture failure (`CaptureFailed { IsTerminal: false }`, a processor 503) is retried forward under the same CommandId, the capture's idempotency key at FakePay. Every earlier step has succeeded and the authorization is still good, so going backwards would throw a sale away over a blip.
- **A terminal capture failure still compensates.** The authorization expired, was voided, or was refused at capture. Nothing was captured, so there is nothing to go forward to, and void, release and cancel all still apply.
- **An unknown capture outcome stops the saga.** When the retries and the inquiry get no answer, or the inquiry finds no trace, the money may still have moved: the capture might be queued behind a backlog. Compensating would cancel a paid order's shipment. Retrying could be refused or, at a processor with weaker idempotency, charge twice. The saga parks as `NeedsManualReview`, the `SagaNeedsAttention` alert fires, and the runbook has a person check the provider before anything else happens.
- **When capture succeeds, the saga ends.** It publishes `OrderCompleted` and is finished. A refund after that is a returns flow: a new business process started by a customer, with its own rules. It is out of scope here and deliberately not part of this saga.

## Consequences

- The authorization has to outlive the saga. FakePay holds one for 7 days. The saga's retry and compensation budgets are measured in minutes (`SagaTimings`), so a hold never expires under a saga that is still working. A real business that captures on dispatch, days later, would have to handle expired holds by re-authorizing. That is out of scope here.
- Each branch past the pivot has an integration test in `PivotAndCompensationTests`:
  - Transient: `tok_flaky_capture`, `CaptureTransientFailure_RetriesForward_Completes`.
  - Terminal: `tok_expired_auth`, `CaptureTerminalFailure_CancelsReleasesVoids`.
  - No trace: `tok_capture_down` (every capture gets a 503 and records nothing, so the inquiry finds nothing), `CaptureInquiryFindsNothing_StopsForManualReview_AuthorizationKept`.
  - No answer: Toxiproxy drops FakePay's responses, so the capture lands but neither it nor the inquiry is answered, `CaptureOutcomeUnknown_StopsForManualReview_ShipmentKept`.
- Payments refuses a capture it holds no live authorization for (`no live authorization`, terminal). That is what makes the operator's cancel of a parked saga safe: the void goes first, so any capture that turns up later is refused.
