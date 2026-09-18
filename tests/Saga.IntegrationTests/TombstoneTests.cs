using Contracts;
using FakePay.Data;
using Inventory;
using Payments.Data;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

// A compensation can overtake the command it undoes: the forward command sits in a
// backed-up queue, or is still inside the provider call, when the saga gives up on it.
// These tests drive each ordering deterministically and check nothing is left behind.
public class TombstoneTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: void processed first, then the delayed authorize arrives and
    // would create a hold nobody will ever release.
    [Fact]
    public async Task VoidBeforeAuthorize_LateAuthorizeRejected_NothingInTheLedger()
    {
        var sagaId = Guid.CreateVersion7();

        await SendFromOrders(new VoidPayment(sagaId, CommandId.For(sagaId, StepNames.AuthorizePayment, Direction.Compensate)));
        await Eventually(async () => (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))?.Status == PaymentStepStatus.Cancelled);

        await SendFromOrders(new AuthorizePayment(sagaId, AuthorizeKey(sagaId), 40m, "tok_visa"));
        await Cluster.Quiesce();

        (await AuthorizationsFor(sagaId)).ShouldBeEmpty();
        (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))!.Status.ShouldBe(PaymentStepStatus.Cancelled);
        // Rejected cleanly by the tombstone, not by blowing up into the dead letters.
        (await DeadLettersFor(Payments.PaymentsSetup.DatabaseName, sagaId)).ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseBeforeReserve_NoLeakedStock()
    {
        var sku = await SeedSku(available: 3);
        var sagaId = Guid.CreateVersion7();

        await SendFromOrders(new ReleaseStock(sagaId, CommandId.For(sagaId, StepNames.ReserveStock, Direction.Compensate)));
        await Cluster.Quiesce();
        await SendFromOrders(new ReserveStock(sagaId, CommandId.For(sagaId, StepNames.ReserveStock, Direction.Forward),
            [new OrderLine(Guid.CreateVersion7(), sku, 2, 10m)]));
        await Cluster.Quiesce();

        (await StockOf(sku)).ShouldBe(3);
        (await ReservationsFor(sagaId)).ShouldBe(0);
        (await HandledCount(InventorySetup.DatabaseName, "Contracts.ReserveStock", sagaId)).ShouldBe(1);
        (await DeadLettersFor(InventorySetup.DatabaseName, sagaId)).ShouldBe(0);
    }

    // Production failure: FakePay is slower than our client timeout. Payments gives up
    // (row stays Pending), the saga moves to compensate, and the void's lookup runs
    // BEFORE FakePay commits the charge. "Not found" at that moment is not an answer:
    // the void must wait out the settle window, find the late charge and void it.
    // (tok_slow_commit holds the request inside FakePay before committing.)
    [Fact]
    public async Task VoidOvertakesInFlightAuthorize_BeforeCommit_ChargeLandsAndIsVoided()
    {
        var sagaId = Guid.CreateVersion7();

        await SendFromOrders(new AuthorizePayment(sagaId, AuthorizeKey(sagaId), 40m, "tok_slow_commit"));
        await Eventually(async () => (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))?.Status == PaymentStepStatus.Pending);
        await SendFromOrders(new VoidPayment(sagaId, CommandId.For(sagaId, StepNames.AuthorizePayment, Direction.Compensate)));

        await Eventually(async () => (await AuthorizationsFor(sagaId)) is [{ Status: AuthorizationStatus.Voided }]);
        await Cluster.Quiesce();
        (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))!.Status.ShouldBe(PaymentStepStatus.Voided);
    }

    // Same race, the other side of the commit: the charge exists but its response hasn't
    // come back. The void finds it by idempotency key and voids it.
    [Fact]
    public async Task VoidOvertakesInFlightAuthorize_AfterCommit_VoidFindsChargeByKey()
    {
        var sagaId = Guid.CreateVersion7();

        await SendFromOrders(new AuthorizePayment(sagaId, AuthorizeKey(sagaId), 40m, "tok_slow"));
        await Eventually(async () => (await AuthorizationsFor(sagaId)).Count == 1);
        await SendFromOrders(new VoidPayment(sagaId, CommandId.For(sagaId, StepNames.AuthorizePayment, Direction.Compensate)));

        await Eventually(async () => (await AuthorizationsFor(sagaId)) is [{ Status: AuthorizationStatus.Voided }]);
        await Cluster.Quiesce();
        (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))!.Status.ShouldBe(PaymentStepStatus.Voided);
    }
}
