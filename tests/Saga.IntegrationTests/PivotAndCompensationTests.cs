using Contracts;
using FakePay.Data;
using Microsoft.EntityFrameworkCore;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;
using Shipping.Data;

namespace Saga.IntegrationTests;

public class PivotAndCompensationTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: a processor blip at capture. Everything before it succeeded;
    // throwing the sale away over one 503 would be the wrong call. (tok_flaky_capture
    // answers the first capture with a 503, then behaves.)
    [Fact]
    public async Task CaptureTransientFailure_RetriesForward_Completes()
    {
        var sku = await SeedSku(available: 5);

        var sagaId = await PlaceOrder(sku, cardToken: "tok_flaky_capture");
        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        var authorization = (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
        authorization.Status.ShouldBe(AuthorizationStatus.Captured);
        // -1 means "fail the next capture"; 0 means that failure was served and a later
        // attempt captured. Proof the first capture really failed and was retried.
        authorization.CaptureAttempts.ShouldBe(0);
        (await StockOf(sku)).ShouldBe(4);
    }

    // Production failure: the authorization expired while the order was being prepared.
    // Nothing can go forward, so everything goes back: shipment cancelled, stock
    // returned, authorization voided, in reverse order.
    [Fact]
    public async Task CaptureTerminalFailure_CancelsReleasesVoids()
    {
        var sku = await SeedSku(available: 5);

        var sagaId = await PlaceOrder(sku, quantity: 2, cardToken: "tok_expired_auth");
        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Cancelled, Describe(saga));
        saga.FailureReason.ShouldBe("capture failed: authorization_expired");
        saga.Journal.Select(j => (j.Step, j.Outcome)).ShouldBe(
        [
            (StepNames.AuthorizePayment, StepOutcome.Compensated),
            (StepNames.ReserveStock, StepOutcome.Compensated),
            (StepNames.BookShipment, StepOutcome.Compensated),
            (StepNames.CapturePayment, StepOutcome.Rejected),
        ]);
        (await StockOf(sku)).ShouldBe(5);
        (await ReservationsFor(sagaId)).ShouldBe(0);
        (await ShipmentFor(sagaId))!.Status.ShouldBe(ShipmentStatus.Cancelled);
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Voided);
    }

    // Out of stock is a business answer: the saga turns around cleanly, and the only
    // thing to undo is the authorization.
    [Fact]
    public async Task InsufficientStock_VoidsAuthorization_NothingDeadLettered()
    {
        var sku = await SeedSku(available: 1);

        var sagaId = await PlaceOrder(sku, quantity: 3);
        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Cancelled, Describe(saga));
        saga.FailureReason.ShouldBe($"insufficient stock for {sku}");
        (await StockOf(sku)).ShouldBe(1);
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Voided);
        (await DeadLettersFor(Inventory.InventorySetup.DatabaseName, sagaId)).ShouldBe(0);
        (await DeadLettersFor(Orders.OrdersSetup.DatabaseName, sagaId)).ShouldBe(0);
    }

    // Production failure: Payments is down for longer than the compensation budget. A
    // live authorization is left behind, so the saga must end in the one state that
    // pages a human, not pretend it cancelled cleanly.
    [Fact]
    public async Task CompensationExhausted_EndsCompensationFailed()
    {
        var sku = await SeedSku(available: 0);
        await Cluster.Stop(Service.Inventory);

        var sagaId = await PlaceOrder(sku);
        await WaitForSaga(sagaId, s => s.CurrentStep == StepNames.ReserveStock);
        await Cluster.Stop(Service.Payments);
        await Cluster.Start(Service.Inventory);

        var saga = await WaitForSaga(sagaId, s => s.Status == SagaStatus.CompensationFailed, TimeSpan.FromSeconds(60));

        saga.FailureReason.ShouldNotBeNull().ShouldContain("compensation of authorize-payment unanswered");
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Authorized);
        // (Payments restarts for the next test and drains the queued voids; the hold is
        // released then, which is what the runbook's manual replay would achieve.)
    }

    // Past the pivot, the inquiry gets a real answer: FakePay has no capture for the key
    // (every capture got a 503 and was never recorded). "No trace" is still not safe to
    // act on backwards: a capture queued behind a backlog could land after we cancelled
    // the shipment. The saga stops for a human instead of compensating.
    [Fact]
    public async Task CaptureInquiryFindsNothing_StopsForManualReview_AuthorizationKept()
    {
        var sku = await SeedSku(available: 5);

        var sagaId = await PlaceOrder(sku, cardToken: "tok_capture_down");
        var saga = await WaitForSaga(sagaId, s => s.Status == SagaStatus.NeedsManualReview, TimeSpan.FromSeconds(60));

        saga.FailureReason.ShouldNotBeNull().ShouldContain("inquiry found no trace");
        (await HandledCount(Payments.PaymentsSetup.DatabaseName, "Contracts.CheckStepStatus", sagaId)).ShouldBe(1);
        (await ShipmentFor(sagaId))!.Status.ShouldBe(ShipmentStatus.Booked);
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Authorized);
    }

    // Past the pivot with nobody answering: every capture response (and the inquiry) is
    // lost, while the capture itself goes through at FakePay. The money WAS taken here;
    // going backwards would cancel the shipment of a paid order. The saga stops instead.
    [Fact]
    public async Task CaptureOutcomeUnknown_StopsForManualReview_ShipmentKept()
    {
        var sku = await SeedSku(available: 5);
        await Cluster.Stop(Service.Shipping);
        var sagaId = await PlaceOrder(sku);
        await WaitForSaga(sagaId, s => s.CurrentStep == StepNames.BookShipment);

        await Cluster.Toxiproxy.DropResponses(SagaCluster.FakePayProxy);
        await Cluster.Start(Service.Shipping);
        var saga = await WaitForSaga(sagaId, s => s.Status == SagaStatus.NeedsManualReview, TimeSpan.FromSeconds(60));
        await Cluster.Toxiproxy.RemoveToxic(SagaCluster.FakePayProxy);

        saga.Journal.ShouldNotContain(j => j.Outcome == StepOutcome.Compensated);
        (await ShipmentFor(sagaId))!.Status.ShouldBe(ShipmentStatus.Booked);
        (await ReservationsFor(sagaId)).ShouldBe(1);
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Captured);
    }
}
