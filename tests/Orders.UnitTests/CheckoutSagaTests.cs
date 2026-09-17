using Contracts;
using Orders.Saga;

namespace Orders.UnitTests;

public class CheckoutSagaTests
{
    private readonly SagaHarness _h = new();

    [Fact]
    public void HappyPath_AuthorizeReserveBookCapture_Completes()
    {
        _h.Start().Single<AuthorizePayment>().CommandId.ShouldBe(_h.Fwd(StepNames.AuthorizePayment));

        _h.Saga.Handle(new PaymentAuthorized(_h.Saga.Id, _h.Fwd(StepNames.AuthorizePayment), "auth_1"), _h.Runtime)
            .Single<ReserveStock>();
        _h.Saga.Handle(new StockReserved(_h.Saga.Id, _h.Fwd(StepNames.ReserveStock)), _h.Runtime)
            .Single<BookShipment>();
        _h.Saga.Handle(new ShipmentBooked(_h.Saga.Id, _h.Fwd(StepNames.BookShipment), "TRK1"), _h.Runtime)
            .Single<CapturePayment>().Amount.ShouldBe(60m);
        var done = _h.Saga.Handle(new PaymentCaptured(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), "cap_1"), _h.Runtime);

        done.Single<OrderCompleted>().Amount.ShouldBe(60m);
        _h.Saga.Status.ShouldBe(SagaStatus.Completed);
        _h.Saga.Journal.ShouldAllBe(e => e.Outcome == StepOutcome.Succeeded);
        _h.Saga.Journal.Select(e => e.Step).ShouldBe(
            [StepNames.AuthorizePayment, StepNames.ReserveStock, StepNames.BookShipment, StepNames.CapturePayment]);
    }

    // Production failure: a client retries POST /orders before the first StartCheckout
    // has been handled, and the second one would start the whole checkout again.
    [Fact]
    public void DuplicateStart_IsIgnored()
    {
        _h.AdvanceTo(StepNames.ReserveStock);

        var again = _h.Saga.StartOrHandle(SagaHarness.StartCommand(_h.Saga.Id), _h.Runtime);

        again.ShouldBeEmpty();
        _h.Saga.CurrentStep.ShouldBe(StepNames.ReserveStock);
    }

    [Fact]
    public void EveryForwardCommand_SchedulesATimeoutForItsOwnAttempt()
    {
        var timeout = _h.Start().Single<StepTimeout>();

        timeout.ShouldBe(new StepTimeout(_h.Saga.Id, StepNames.AuthorizePayment, 1, Direction.Forward, TimeSpan.FromMinutes(1)));
    }

    // Production failure: a participant is slow, and the retry must not create a second effect.
    [Fact]
    public void ForwardTimeout_ResendsSameCommandId_WithLongerTimeout()
    {
        _h.Start();

        var retry = _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 1), _h.Runtime);

        retry.Single<AuthorizePayment>().CommandId.ShouldBe(_h.Fwd(StepNames.AuthorizePayment));
        retry.Single<StepTimeout>().ShouldBe(new StepTimeout(_h.Saga.Id, StepNames.AuthorizePayment, 2, Direction.Forward, TimeSpan.FromMinutes(2)));
        _h.Saga.AttemptCount.ShouldBe(2);
    }

    // Production failure: the timer for attempt 1 fires late, after attempt 2 was sent.
    [Fact]
    public void StaleTimeout_Discarded()
    {
        _h.Start();
        _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 1), _h.Runtime);

        var again = _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 1), _h.Runtime);

        again.ShouldBeEmpty();
        _h.Saga.AttemptCount.ShouldBe(2);
    }

    // Production failure: a forward timer fires while the saga is undoing that same step,
    // and re-sends the forward command into a compensation.
    [Fact]
    public void ForwardTimeoutDuringCompensation_Discarded()
    {
        _h.AdvanceTo(StepNames.ReserveStock);
        _h.Saga.Handle(new InsufficientStock(_h.Saga.Id, _h.Fwd(StepNames.ReserveStock), "BOOK-DDD"), _h.Runtime);
        _h.Saga.CurrentStep.ShouldBe(StepNames.AuthorizePayment);
        _h.Saga.AttemptCount.ShouldBe(1);

        // Same step, same attempt number, wrong direction.
        var stale = _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 1, Direction.Forward), _h.Runtime);

        stale.ShouldBeEmpty();
        _h.Saga.Status.ShouldBe(SagaStatus.Compensating);
    }

    // Production failure: the participant answers attempt 2 from its dedupe table with the
    // reply to attempt 1. If the saga matched on attempt numbers it would hang here.
    [Fact]
    public void CachedReplyAfterRetry_Accepted()
    {
        _h.Start();
        _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 1), _h.Runtime);

        var next = _h.Saga.Handle(new PaymentAuthorized(_h.Saga.Id, _h.Fwd(StepNames.AuthorizePayment), "auth_1"), _h.Runtime);

        next.Single<ReserveStock>();
        _h.Saga.AuthorizationId.ShouldBe("auth_1");
    }

    [Fact]
    public void ReplyForAStepAlreadyPassed_Discarded()
    {
        _h.AdvanceTo(StepNames.BookShipment);

        var duplicate = _h.Saga.Handle(new StockReserved(_h.Saga.Id, _h.Fwd(StepNames.ReserveStock)), _h.Runtime);

        duplicate.ShouldBeEmpty();
        _h.Saga.CurrentStep.ShouldBe(StepNames.BookShipment);
    }

    [Fact]
    public void InsufficientStock_IsABusinessReply_VoidsOnlyTheAuthorization()
    {
        _h.AdvanceTo(StepNames.ReserveStock);

        var comp = _h.Saga.Handle(new InsufficientStock(_h.Saga.Id, _h.Fwd(StepNames.ReserveStock), "BOOK-DDD"), _h.Runtime);

        comp.Single<VoidPayment>().CommandId.ShouldBe(_h.Comp(StepNames.AuthorizePayment));
        comp.Unwrapped().OfType<ReleaseStock>().ShouldBeEmpty();
        _h.Saga.FailureReason.ShouldBe("insufficient stock for BOOK-DDD");

        var end = _h.Saga.Handle(new PaymentVoided(_h.Saga.Id, _h.Comp(StepNames.AuthorizePayment), WasNoOp: false), _h.Runtime);

        end.Single<OrderCancelled>();
        _h.Saga.Status.ShouldBe(SagaStatus.Cancelled);
    }

    [Fact]
    public void ShipmentRejected_CompensatesInReverseOrder()
    {
        _h.AdvanceTo(StepNames.BookShipment);

        _h.Saga.Handle(new ShipmentRejected(_h.Saga.Id, _h.Fwd(StepNames.BookShipment), "undeliverable"), _h.Runtime)
            .Single<ReleaseStock>();
        _h.Saga.Handle(new StockReleased(_h.Saga.Id, _h.Comp(StepNames.ReserveStock), WasNoOp: false), _h.Runtime)
            .Single<VoidPayment>();
        _h.Saga.Handle(new PaymentVoided(_h.Saga.Id, _h.Comp(StepNames.AuthorizePayment), WasNoOp: false), _h.Runtime)
            .Single<OrderCancelled>();

        _h.Saga.Journal.Select(e => (e.Step, e.Outcome)).ShouldBe(
        [
            (StepNames.AuthorizePayment, StepOutcome.Compensated),
            (StepNames.ReserveStock, StepOutcome.Compensated),
            (StepNames.BookShipment, StepOutcome.Rejected),
        ]);
    }

    [Fact]
    public void RetriesExhausted_AsksTheOwningParticipant()
    {
        _h.AdvanceTo(StepNames.ReserveStock);
        _h.Saga.Handle(_h.Timeout(StepNames.ReserveStock, 1), _h.Runtime);
        _h.Saga.Handle(_h.Timeout(StepNames.ReserveStock, 2), _h.Runtime);

        var inquiry = _h.Saga.Handle(_h.Timeout(StepNames.ReserveStock, 3), _h.Runtime);

        inquiry.Single<CheckStepStatus>().ShouldBe(new CheckStepStatus(_h.Saga.Id, _h.Fwd(StepNames.ReserveStock), StepNames.ReserveStock));
        CheckoutSaga.OwnerOf(StepNames.ReserveStock).ShouldBe(Queues.Inventory);
        inquiry.Single<StepTimeout>().Attempt.ShouldBe(4);
        _h.Saga.AwaitingInquiry.ShouldBeTrue();
    }

    // Production failure: FakePay committed the authorization but every response was
    // lost. Compensating blind would leave the money held; asking finds it, and the saga
    // carries on as if the reply had arrived.
    [Fact]
    public void AuthorizeTimesOut_ChargeLanded_InquiryFindsIt_ContinuesForward()
    {
        ExhaustAuthorizeRetries();

        var next = _h.Saga.Handle(new StepStatusReported(_h.Saga.Id, _h.Fwd(StepNames.AuthorizePayment), StepNames.AuthorizePayment, InquiryResult.Succeeded, "auth_9"), _h.Runtime);

        next.Single<ReserveStock>();
        _h.Saga.AuthorizationId.ShouldBe("auth_9");
        _h.Saga.Status.ShouldBe(SagaStatus.InProgress);
    }

    // Nothing found does not mean nothing happened: the command may still be in flight.
    // The step stays Unknown and is compensated, and the tombstone does the rest.
    [Fact]
    public void InquiryFindsNothing_StepStaysUnknown_AndIsStillCompensated()
    {
        ExhaustAuthorizeRetries();

        var comp = _h.Saga.Handle(new StepStatusReported(_h.Saga.Id, _h.Fwd(StepNames.AuthorizePayment), StepNames.AuthorizePayment, InquiryResult.NotFound, null), _h.Runtime);

        comp.Single<VoidPayment>();
        _h.Saga.Status.ShouldBe(SagaStatus.Compensating);
        _h.Saga.Journal.Single().Outcome.ShouldBe(StepOutcome.Unknown);
    }

    [Fact]
    public void InquiryUnanswered_CompensatesTheUnknownStep()
    {
        ExhaustAuthorizeRetries();

        var comp = _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 4), _h.Runtime);

        comp.Single<VoidPayment>();
        _h.Saga.Status.ShouldBe(SagaStatus.Compensating);
    }

    [Fact]
    public void CompensationFindsNothingToUndo_RecordedAsNoOp()
    {
        ExhaustAuthorizeRetries();
        _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, 4), _h.Runtime);

        _h.Saga.Handle(new PaymentVoided(_h.Saga.Id, _h.Comp(StepNames.AuthorizePayment), WasNoOp: true), _h.Runtime);

        _h.Saga.Status.ShouldBe(SagaStatus.Cancelled);
        _h.Saga.Journal.Single().Outcome.ShouldBe(StepOutcome.NothingToCompensate);
    }

    // The pivot: a blip at capture must not throw away a sale that is otherwise done.
    [Fact]
    public void CaptureTransientFailure_RetriesForward()
    {
        _h.AdvanceTo(StepNames.CapturePayment);

        _h.Saga.Handle(new CaptureFailed(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), IsTerminal: false, "HTTP 503"), _h.Runtime)
            .ShouldBeEmpty();
        _h.Saga.Status.ShouldBe(SagaStatus.InProgress);

        var retry = _h.Saga.Handle(_h.Timeout(StepNames.CapturePayment, 1), _h.Runtime);
        retry.Single<CapturePayment>().CommandId.ShouldBe(_h.Fwd(StepNames.CapturePayment));
        _h.Saga.Handle(new PaymentCaptured(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), "cap_1"), _h.Runtime)
            .Single<OrderCompleted>();
    }

    [Fact]
    public void CaptureTerminalFailure_CancelsReleasesVoids()
    {
        _h.AdvanceTo(StepNames.CapturePayment);

        _h.Saga.Handle(new CaptureFailed(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), IsTerminal: true, "authorization_expired"), _h.Runtime)
            .Single<CancelShipment>();
        _h.Saga.Handle(new ShipmentCancelled(_h.Saga.Id, _h.Comp(StepNames.BookShipment), WasNoOp: false), _h.Runtime)
            .Single<ReleaseStock>();
        _h.Saga.Handle(new StockReleased(_h.Saga.Id, _h.Comp(StepNames.ReserveStock), WasNoOp: false), _h.Runtime)
            .Single<VoidPayment>();
        _h.Saga.Handle(new PaymentVoided(_h.Saga.Id, _h.Comp(StepNames.AuthorizePayment), WasNoOp: false), _h.Runtime)
            .Single<OrderCancelled>().Reason.ShouldBe("capture failed: authorization_expired");
    }

    // Production failure: FakePay goes dark in the middle of capture. The money may have
    // been taken; cancelling the shipment and releasing stock of a paid order is worse
    // than stopping and asking a human.
    [Fact]
    public void CaptureOutcomeUnknownPastThePivot_StopsForManualReview_DoesNotCompensate()
    {
        _h.AdvanceTo(StepNames.CapturePayment);
        for (var attempt = 1; attempt <= _h.Timings.ForwardAttemptTimeouts.Length; attempt++)
        {
            _h.Saga.Handle(_h.Timeout(StepNames.CapturePayment, attempt), _h.Runtime);
        }

        var result = _h.Saga.Handle(_h.Timeout(StepNames.CapturePayment, _h.Timings.ForwardAttemptTimeouts.Length + 1), _h.Runtime);

        result.ShouldBeEmpty();
        _h.Saga.Status.ShouldBe(SagaStatus.NeedsManualReview);
        _h.Saga.Journal.ShouldNotContain(e => e.Outcome == StepOutcome.Compensated);
    }

    // Production failure: the capture is queued behind a Payments backlog when the
    // inquiry runs. "No trace" today does not mean no capture tomorrow.
    [Fact]
    public void CaptureInquiryFindsNothing_StopsForManualReview_DoesNotCompensate()
    {
        _h.AdvanceTo(StepNames.CapturePayment);
        for (var attempt = 1; attempt <= _h.Timings.ForwardAttemptTimeouts.Length; attempt++)
        {
            _h.Saga.Handle(_h.Timeout(StepNames.CapturePayment, attempt), _h.Runtime);
        }

        var result = _h.Saga.Handle(new StepStatusReported(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), StepNames.CapturePayment, InquiryResult.NotFound, null), _h.Runtime);

        result.ShouldBeEmpty();
        _h.Saga.Status.ShouldBe(SagaStatus.NeedsManualReview);
    }

    // Production failure: the payment service is down for longer than the compensation
    // budget. A live authorization is left behind, and a human has to be told.
    [Fact]
    public void CompensationExhausted_EndsInCompensationFailed()
    {
        _h.AdvanceTo(StepNames.ReserveStock);
        _h.Saga.Handle(new InsufficientStock(_h.Saga.Id, _h.Fwd(StepNames.ReserveStock), "BOOK-DDD"), _h.Runtime);

        for (var attempt = 1; attempt < _h.Timings.CompensationAttemptTimeouts.Length; attempt++)
        {
            _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, attempt, Direction.Compensate), _h.Runtime)
                .Single<VoidPayment>().CommandId.ShouldBe(_h.Comp(StepNames.AuthorizePayment));
        }

        _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, _h.Timings.CompensationAttemptTimeouts.Length, Direction.Compensate), _h.Runtime)
            .ShouldBeEmpty();

        _h.Saga.Status.ShouldBe(SagaStatus.CompensationFailed);
        _h.Saga.FailureReason.ShouldNotBeNull().ShouldContain("compensation of authorize-payment unanswered");
    }

    [Fact]
    public void FinishedSaga_IgnoresEverything()
    {
        _h.AdvanceTo(StepNames.CapturePayment);
        _h.Saga.Handle(new PaymentCaptured(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), "cap_1"), _h.Runtime);

        _h.Saga.Handle(_h.Timeout(StepNames.CapturePayment, 1), _h.Runtime).ShouldBeEmpty();
        _h.Saga.Handle(new PaymentCaptured(_h.Saga.Id, _h.Fwd(StepNames.CapturePayment), "cap_1"), _h.Runtime).ShouldBeEmpty();
        _h.Saga.Status.ShouldBe(SagaStatus.Completed);
    }

    [Fact]
    public void LastUpdated_MovesOnTransitions_NotOnStaleMessages()
    {
        _h.Start();
        var before = _h.Saga.LastUpdatedUtc;
        _h.Clock.Advance(TimeSpan.FromMinutes(5));

        _h.Saga.Handle(_h.Timeout(StepNames.ReserveStock, 1), _h.Runtime);

        // A stuck saga must stay visibly stuck to the monitor, however many stale
        // messages bounce off it.
        _h.Saga.LastUpdatedUtc.ShouldBe(before);
    }

    private void ExhaustAuthorizeRetries()
    {
        _h.Start();
        for (var attempt = 1; attempt <= _h.Timings.ForwardAttemptTimeouts.Length; attempt++)
        {
            _h.Saga.Handle(_h.Timeout(StepNames.AuthorizePayment, attempt), _h.Runtime);
        }
    }
}
