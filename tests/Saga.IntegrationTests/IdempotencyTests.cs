using Contracts;
using Microsoft.EntityFrameworkCore;
using Payments.Data;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

// The business idempotency layer (ADR 0002): a retry arrives as a NEW transport message
// (new MessageId, so Wolverine's inbox lets it through) carrying the SAME CommandId.
// Only the participant's (SagaId, Step) record stands between that and a second effect.
public class IdempotencyTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: the saga's timeout fires just as the first authorize is being
    // processed, and both copies hit Payments at the same moment.
    [Fact]
    public async Task RetryAfterTimeout_NewMessageId_SameCommandId_AuthorizesOnce()
    {
        var sagaId = Guid.CreateVersion7();
        var command = new AuthorizePayment(sagaId, AuthorizeKey(sagaId), 40m, "tok_visa");

        await Task.WhenAll(SendFromOrders(command), SendFromOrders(command), SendFromOrders(command));

        await Eventually(async () => (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))?.Status == PaymentStepStatus.Authorized);
        await Cluster.Quiesce();

        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
        await using var fakePay = FakePayDb();
        var key = AuthorizeKey(sagaId).ToString("D");
        var record = await fakePay.IdempotencyKeys.SingleAsync(k => k.Key == key && k.Operation == "authorize");
        record.StatusCode.ShouldBe(201);
    }

    // Production failure: a reserve is redelivered after the saga already moved on.
    // Stock must be taken once, and the duplicate answered from the stored outcome.
    [Fact]
    public async Task DuplicateReserve_ReturnsStoredOutcome_StockTakenOnce()
    {
        var sku = await SeedSku(available: 5);
        var sagaId = await PlaceOrder(sku, quantity: 2);
        var saga = await WaitForSaga(sagaId, s => s.Status == Orders.Saga.SagaStatus.Completed);
        await SendFromOrders(new ReserveStock(sagaId, CommandId.For(sagaId, StepNames.ReserveStock, Direction.Forward), saga.Lines));
        await SendFromOrders(new ReserveStock(sagaId, CommandId.For(sagaId, StepNames.ReserveStock, Direction.Forward), saga.Lines));
        await Cluster.Quiesce();

        (await StockOf(sku)).ShouldBe(3);
        (await ReservationsFor(sagaId)).ShouldBe(1);
        (await HandledCount(InventorySetupDb, "Contracts.ReserveStock", sagaId)).ShouldBe(3);
        (await DeadLettersFor(InventorySetupDb, sagaId)).ShouldBe(0);
    }

    private static string InventorySetupDb => Inventory.InventorySetup.DatabaseName;
}
