using Contracts;
using Microsoft.EntityFrameworkCore;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

public class HappyPathTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Every service, the real broker, the real outbox: the baseline the failure tests
    // are measured against.
    [Fact]
    public async Task Checkout_CompletesEveryStep_AndTheCustomerIsNotified()
    {
        var sku = await SeedSku(available: 10);

        var sagaId = await PlaceOrder(sku, quantity: 2);
        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        saga.Journal.Select(j => (j.Step, j.Outcome)).ShouldBe(
        [
            (StepNames.AuthorizePayment, StepOutcome.Succeeded),
            (StepNames.ReserveStock, StepOutcome.Succeeded),
            (StepNames.BookShipment, StepOutcome.Succeeded),
            (StepNames.CapturePayment, StepOutcome.Succeeded),
        ]);

        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(FakePay.Data.AuthorizationStatus.Captured);
        (await StockOf(sku)).ShouldBe(8);
        (await ReservationsFor(sagaId)).ShouldBe(1);
        (await ShipmentFor(sagaId)).ShouldNotBeNull().Status.ShouldBe(Shipping.Data.ShipmentStatus.Booked);

        await Eventually(async () =>
        {
            await using var db = NotificationsDb();
            return await db.Sent.AnyAsync(n => n.OrderId == sagaId && n.Kind == "completed");
        });
    }
}
