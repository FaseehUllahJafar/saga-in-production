using System.Diagnostics;
using Contracts;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Orders.Saga;

// The orchestrator. Wolverine does the plumbing (load this row by SagaId, run the
// handler, save the row and the outgoing messages in one transaction, schedule the
// timeouts durably). Everything that decides WHAT happens is in this file, by hand.
//
//   Authorize ──► Reserve ──► Book ──► Capture ──► Completed
//      │             │          │         │ (pivot: after this there is no going back)
//      ▼             ▼          ▼         ▼ terminal failure only
//    Void  ◄──── Release ◄─── Cancel ◄────┘            ──► Cancelled
//
// Authorize-then-capture rather than charge-then-refund: a void before capture is free
// and invisible to the customer, a refund is neither. See docs/adr/0005.
public sealed class CheckoutSaga : Wolverine.Saga
{
    public Guid Id { get; set; }

    public string CustomerEmail { get; set; } = "";
    public string CardToken { get; set; } = "";
    public string ShippingAddress { get; set; } = "";
    public decimal Amount { get; set; }
    public List<OrderLine> Lines { get; set; } = [];

    public SagaStatus Status { get; set; }
    public string CurrentStep { get; set; } = "";
    public int AttemptCount { get; set; }
    public bool AwaitingInquiry { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public string? FailureReason { get; set; }
    public List<JournalEntry> Journal { get; set; } = [];

    public string? AuthorizationId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? CaptureId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private Direction CurrentDirection => Status == SagaStatus.Compensating ? Direction.Compensate : Direction.Forward;

    private bool IsFinished => Status is SagaStatus.Completed or SagaStatus.Cancelled or SagaStatus.CompensationFailed or SagaStatus.NeedsManualReview;

    // ---- start -------------------------------------------------------------------

    // StartOrHandle, not Start: Wolverine creates the row if it doesn't exist and hands
    // us the existing one if it does. A client retry with the same Idempotency-Key that
    // overtakes the first StartCheckout lands here as a harmless no-op, instead of
    // hitting the primary key and sitting in the dead letters.
    public OutgoingMessages StartOrHandle(StartCheckout command, SagaRuntime rt)
    {
        if (StartedUtc != default)
        {
            rt.Logger.LogInformation("Saga {SagaId}: duplicate StartCheckout ignored", command.SagaId);
            return [];
        }

        Id = command.SagaId;
        CustomerEmail = command.CustomerEmail;
        CardToken = command.CardToken;
        ShippingAddress = command.ShippingAddress;
        Lines = command.Lines.ToList();
        Amount = command.Lines.Sum(l => l.UnitPrice * l.Quantity);
        Status = SagaStatus.InProgress;
        StartedUtc = rt.Clock.GetUtcNow();

        rt.Metrics.Started();
        return BeginForward(StepNames.AuthorizePayment, rt);
    }

    // ---- forward replies ---------------------------------------------------------

    public OutgoingMessages Handle(PaymentAuthorized m, SagaRuntime rt) =>
        OnForwardSucceeded(StepNames.AuthorizePayment, m.CommandId, m.AuthorizationId, rt, m);

    public OutgoingMessages Handle(PaymentDeclined m, SagaRuntime rt) =>
        OnForwardRejected(StepNames.AuthorizePayment, m.CommandId, $"payment declined: {m.Reason}", rt, m);

    public OutgoingMessages Handle(StockReserved m, SagaRuntime rt) =>
        OnForwardSucceeded(StepNames.ReserveStock, m.CommandId, null, rt, m);

    // Out of stock is a business answer, not an exception: it arrives as a reply and
    // turns the saga around. It never goes near the retry policy or the dead letters.
    public OutgoingMessages Handle(InsufficientStock m, SagaRuntime rt) =>
        OnForwardRejected(StepNames.ReserveStock, m.CommandId, $"insufficient stock for {m.Sku}", rt, m);

    public OutgoingMessages Handle(ShipmentBooked m, SagaRuntime rt) =>
        OnForwardSucceeded(StepNames.BookShipment, m.CommandId, m.TrackingNumber, rt, m);

    public OutgoingMessages Handle(ShipmentRejected m, SagaRuntime rt) =>
        OnForwardRejected(StepNames.BookShipment, m.CommandId, $"shipment rejected: {m.Reason}", rt, m);

    public OutgoingMessages Handle(PaymentCaptured m, SagaRuntime rt) =>
        OnForwardSucceeded(StepNames.CapturePayment, m.CommandId, m.CaptureId, rt, m);

    // The pivot. A transient capture failure is retried FORWARD: every earlier step has
    // succeeded and the authorization is still good, so going backwards would throw away
    // a sale over a blip. The next StepTimeout re-sends the capture with the same
    // CommandId. Only a terminal failure (authorization expired, voided, declined at
    // capture) means there is nothing left to go forward to, so the saga compensates.
    public OutgoingMessages Handle(CaptureFailed m, SagaRuntime rt)
    {
        if (!IsCurrentForward(StepNames.CapturePayment, m.CommandId))
        {
            return Stale(m, rt);
        }

        if (m.IsTerminal)
        {
            return OnForwardRejected(StepNames.CapturePayment, m.CommandId, $"capture failed: {m.Reason}", rt, m);
        }

        rt.Logger.LogWarning("Saga {SagaId}: transient capture failure on attempt {Attempt} ({Reason}); the next timeout retries forward",
            Id, AttemptCount, m.Reason);
        return [];
    }

    // ---- compensation replies ----------------------------------------------------

    public OutgoingMessages Handle(PaymentVoided m, SagaRuntime rt) =>
        OnCompensated(StepNames.AuthorizePayment, m.CommandId, m.WasNoOp, rt, m);

    public OutgoingMessages Handle(StockReleased m, SagaRuntime rt) =>
        OnCompensated(StepNames.ReserveStock, m.CommandId, m.WasNoOp, rt, m);

    public OutgoingMessages Handle(ShipmentCancelled m, SagaRuntime rt) =>
        OnCompensated(StepNames.BookShipment, m.CommandId, m.WasNoOp, rt, m);

    // ---- inquiry -----------------------------------------------------------------

    public OutgoingMessages Handle(StepStatusReported m, SagaRuntime rt)
    {
        if (!IsCurrentForward(m.Step, m.CommandId))
        {
            return Stale(m, rt);
        }

        return m.Result switch
        {
            InquiryResult.Succeeded => OnForwardSucceeded(m.Step, m.CommandId, m.Reference, rt, m),
            InquiryResult.Failed => OnForwardRejected(m.Step, m.CommandId, $"{m.Step} failed (found by inquiry)", rt, m),

            // Nothing found. The command may still be in flight, so the journal keeps
            // the step as Unknown and it WILL be compensated. The participant's
            // tombstone then rejects the late command when it finally arrives.
            InquiryResult.NotFound when AwaitingInquiry =>
                BeginCompensation($"{m.Step}: no reply after {rt.Timings.ForwardAttemptTimeouts.Length} attempts and no trace found", rt),

            _ => Stale(m, rt)
        };
    }

    // ---- timeouts ----------------------------------------------------------------

    // A timeout means "I don't know", never "it failed". Each forward retry re-sends the
    // SAME CommandId, which is itself a safe question: a participant that already did the
    // work answers from its dedupe table instead of doing it again. When the retries run
    // out, the orchestrator asks explicitly (CheckStepStatus) and acts on the answer.
    // It never compensates blind, and when it must compensate without an answer, the
    // step stays Unknown so the compensation still goes out.
    public OutgoingMessages Handle(StepTimeout timeout, SagaRuntime rt)
    {
        if (IsFinished || timeout.Step != CurrentStep || timeout.Attempt != AttemptCount || timeout.Direction != CurrentDirection)
        {
            return Stale(timeout, rt);
        }

        var timings = rt.Timings;

        if (timeout.Direction == Direction.Compensate)
        {
            if (AttemptCount < timings.CompensationAttemptTimeouts.Length)
            {
                return SendCompensation(CurrentStep, AttemptCount + 1, rt);
            }

            // Out of patience on an undo. A real effect may be left in the world (a live
            // authorization, held stock), so this is the one terminal state that pages a
            // human: see the CompensationFailed alert and docs/runbook.md.
            Status = SagaStatus.CompensationFailed;
            FailureReason = $"{FailureReason}; compensation of {CurrentStep} unanswered after {AttemptCount} attempts";
            Touch(rt);
            rt.Metrics.CompensationFailed(CurrentStep);
            rt.Logger.LogError("Saga {SagaId}: compensation of {Step} exhausted after {Attempts} attempts", Id, CurrentStep, AttemptCount);
            return [];
        }

        if (AwaitingInquiry && CurrentStep == StepNames.CapturePayment)
        {
            // Past the pivot with no answer at all: the money may have been taken. Going
            // backwards would cancel the shipment and release the stock of a paid order,
            // and the void would then fail anyway. Stop and hand it to a human.
            return NeedsManualReview("capture outcome unknown: retries and inquiry unanswered", rt);
        }

        if (AwaitingInquiry)
        {
            return BeginCompensation($"{CurrentStep}: no reply after {timings.ForwardAttemptTimeouts.Length} attempts and the inquiry went unanswered", rt);
        }

        if (AttemptCount < timings.ForwardAttemptTimeouts.Length)
        {
            return SendForward(CurrentStep, AttemptCount + 1, rt);
        }

        AwaitingInquiry = true;
        AttemptCount++;
        Touch(rt);
        var commandId = CommandId.For(Id, CurrentStep, Direction.Forward);
        return
        [
            new CheckStepStatus(Id, commandId, CurrentStep).ToEndpoint(OwnerOf(CurrentStep)),
            new StepTimeout(Id, CurrentStep, AttemptCount, Direction.Forward, timings.InquiryTimeout)
        ];
    }

    // ---- transitions -------------------------------------------------------------

    private static string? NextStep(string step) => step switch
    {
        StepNames.AuthorizePayment => StepNames.ReserveStock,
        StepNames.ReserveStock => StepNames.BookShipment,
        StepNames.BookShipment => StepNames.CapturePayment,
        StepNames.CapturePayment => null,
        _ => throw new UnreachableException($"unknown step {step}")
    };

    private OutgoingMessages OnForwardSucceeded(string step, Guid commandId, string? reference, SagaRuntime rt, object message)
    {
        if (!IsCurrentForward(step, commandId))
        {
            return Stale(message, rt);
        }

        Journal.Record(step, StepOutcome.Succeeded, rt.Clock.GetUtcNow(), reference);
        switch (step)
        {
            case StepNames.AuthorizePayment: AuthorizationId = reference; break;
            case StepNames.BookShipment: TrackingNumber = reference; break;
            case StepNames.CapturePayment: CaptureId = reference; break;
        }

        var next = NextStep(step);
        return next is null ? Complete(rt) : BeginForward(next, rt);
    }

    private OutgoingMessages OnForwardRejected(string step, Guid commandId, string reason, SagaRuntime rt, object message)
    {
        if (!IsCurrentForward(step, commandId))
        {
            return Stale(message, rt);
        }

        Journal.Record(step, StepOutcome.Rejected, rt.Clock.GetUtcNow());
        return BeginCompensation(reason, rt);
    }

    private OutgoingMessages OnCompensated(string step, Guid commandId, bool wasNoOp, SagaRuntime rt, object message)
    {
        if (Status != SagaStatus.Compensating || step != CurrentStep || commandId != CommandId.For(Id, step, Direction.Compensate))
        {
            return Stale(message, rt);
        }

        Journal.Record(step, wasNoOp ? StepOutcome.NothingToCompensate : StepOutcome.Compensated, rt.Clock.GetUtcNow());
        return CompensateNextOrFinish(rt);
    }

    private OutgoingMessages BeginForward(string step, SagaRuntime rt)
    {
        Journal.Record(step, StepOutcome.Unknown, rt.Clock.GetUtcNow());
        return SendForward(step, attempt: 1, rt);
    }

    private OutgoingMessages SendForward(string step, int attempt, SagaRuntime rt)
    {
        CurrentStep = step;
        AttemptCount = attempt;
        AwaitingInquiry = false;
        Touch(rt);

        var commandId = CommandId.For(Id, step, Direction.Forward);
        object command = step switch
        {
            StepNames.AuthorizePayment => new AuthorizePayment(Id, commandId, Amount, CardToken),
            StepNames.ReserveStock => new ReserveStock(Id, commandId, Lines),
            StepNames.BookShipment => new BookShipment(Id, commandId, ShippingAddress, Lines.Sum(l => l.Quantity)),
            StepNames.CapturePayment => new CapturePayment(Id, commandId, Amount),
            _ => throw new UnreachableException($"unknown step {step}")
        };

        var timeout = rt.Timings.ForwardAttemptTimeouts[attempt - 1];
        return [command, new StepTimeout(Id, step, attempt, Direction.Forward, timeout)];
    }

    private OutgoingMessages BeginCompensation(string reason, SagaRuntime rt)
    {
        rt.Metrics.CompensationStarted(CurrentStep);
        rt.Logger.LogWarning("Saga {SagaId}: compensating after {Step}: {Reason}", Id, CurrentStep, reason);

        Status = SagaStatus.Compensating;
        FailureReason = reason;
        return CompensateNextOrFinish(rt);
    }

    private OutgoingMessages CompensateNextOrFinish(SagaRuntime rt)
    {
        var next = Journal.NextToCompensate();
        if (next is not null)
        {
            return SendCompensation(next.Step, attempt: 1, rt);
        }

        Status = SagaStatus.Cancelled;
        CurrentStep = "";
        AttemptCount = 0;
        Touch(rt);
        return [new OrderCancelled(Id, CustomerEmail, FailureReason ?? "cancelled")];
    }

    private OutgoingMessages SendCompensation(string step, int attempt, SagaRuntime rt)
    {
        CurrentStep = step;
        AttemptCount = attempt;
        AwaitingInquiry = false;
        Touch(rt);

        var commandId = CommandId.For(Id, step, Direction.Compensate);
        object command = step switch
        {
            StepNames.AuthorizePayment => new VoidPayment(Id, commandId),
            StepNames.ReserveStock => new ReleaseStock(Id, commandId),
            StepNames.BookShipment => new CancelShipment(Id, commandId),
            _ => throw new UnreachableException($"{step} has no compensation")
        };

        var timeout = rt.Timings.CompensationAttemptTimeouts[attempt - 1];
        return [command, new StepTimeout(Id, step, attempt, Direction.Compensate, timeout)];
    }

    private OutgoingMessages NeedsManualReview(string reason, SagaRuntime rt)
    {
        Status = SagaStatus.NeedsManualReview;
        FailureReason = reason;
        Touch(rt);
        rt.Metrics.ManualReview(CurrentStep);
        rt.Logger.LogError("Saga {SagaId}: needs manual review at {Step}: {Reason}", Id, CurrentStep, reason);
        return [];
    }

    private OutgoingMessages Complete(SagaRuntime rt)
    {
        Status = SagaStatus.Completed;
        CurrentStep = "";
        AttemptCount = 0;
        Touch(rt);
        rt.Metrics.Completed();

        // Notifications live outside the saga. The saga's job ends when the order is
        // paid for and booked; a failing email provider must not hold it open, let
        // alone compensate a completed order. See src/Notifications.
        return [new OrderCompleted(Id, CustomerEmail, Amount)];
    }

    // ---- gating ------------------------------------------------------------------

    // Replies are accepted on (SagaId, step, CommandId) alone. Deliberately NOT on the
    // attempt number: a retry is answered from the participant's dedupe table with the
    // reply to attempt 1, and that is still the right answer.
    private bool IsCurrentForward(string step, Guid commandId) =>
        Status == SagaStatus.InProgress &&
        step == CurrentStep &&
        commandId == CommandId.For(Id, step, Direction.Forward);

    private OutgoingMessages Stale(object message, SagaRuntime rt)
    {
        var type = message.GetType().Name;
        rt.Metrics.StaleDiscarded(type);
        rt.Logger.LogInformation("Saga {SagaId}: discarded stale {MessageType} (status {Status}, step {Step}, attempt {Attempt})",
            Id, type, Status, CurrentStep, AttemptCount);
        return [];
    }

    private void Touch(SagaRuntime rt) => LastUpdatedUtc = rt.Clock.GetUtcNow();

    internal static string OwnerOf(string step) => step switch
    {
        StepNames.AuthorizePayment or StepNames.CapturePayment => Queues.Payments,
        StepNames.ReserveStock => Queues.Inventory,
        StepNames.BookShipment => Queues.Shipping,
        _ => throw new UnreachableException($"unknown step {step}")
    };
}
