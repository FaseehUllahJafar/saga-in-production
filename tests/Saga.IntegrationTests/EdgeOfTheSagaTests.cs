using Contracts;
using Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

// What happens at the saga's edges: poison input, the inquiry path to each participant,
// and the notification side-channel that must never hold the saga hostage.
public class EdgeOfTheSagaTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: a malformed command (a producer bug, a bad manual replay).
    // It must land in the dead letters once, not spin in a retry loop or vanish.
    [Fact]
    public async Task PoisonMessage_LandsInDeadLettersOnce()
    {
        var sagaId = Guid.CreateVersion7();

        await SendFromOrders(new ReserveStock(sagaId, CommandId.For(sagaId, StepNames.ReserveStock, Direction.Forward), null!));

        await Eventually(async () => await DeadLettersFor(InventorySetup.DatabaseName, sagaId) == 1);
        await Cluster.Quiesce();
        (await DeadLettersFor(InventorySetup.DatabaseName, sagaId)).ShouldBe(1);
        (await ReservationsFor(sagaId)).ShouldBe(0);
    }

    // The inquiry is addressed per step (ToEndpoint), so it must reach Inventory, not
    // just Payments. Inventory is down for every forward attempt; its queue fills with
    // the three retries and then the inquiry. When it comes back it handles them in
    // parallel, so the inquiry may answer "not found" before a queued reserve commits.
    // That is the real race, and either ending is legitimate. What must hold in both:
    // the inquiry was delivered and answered, and no stock is leaked or lost.
    [Fact]
    public async Task InquiryReachesInventory_AndNoStockIsLeakedEitherWay()
    {
        var sku = await SeedSku(available: 5);
        await Cluster.Stop(Service.Inventory);

        var sagaId = await PlaceOrder(sku);
        await WaitForSaga(sagaId, s => s.CurrentStep == StepNames.ReserveStock && s.AwaitingInquiry, TimeSpan.FromSeconds(60));
        await Cluster.Start(Service.Inventory);

        var saga = await WaitForFinished(sagaId);

        (await HandledCount(InventorySetup.DatabaseName, "Contracts.CheckStepStatus", sagaId)).ShouldBe(1);
        if (saga.Status == SagaStatus.Completed)
        {
            (await ReservationsFor(sagaId)).ShouldBe(1);
            (await StockOf(sku)).ShouldBe(4);
        }
        else
        {
            saga.Status.ShouldBe(SagaStatus.Cancelled, Describe(saga));
            (await ReservationsFor(sagaId)).ShouldBe(0);
            (await StockOf(sku)).ShouldBe(5);
            (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(FakePay.Data.AuthorizationStatus.Voided);
        }
    }

    // Production failure: the email provider is down. The order is paid and booked, so
    // the saga completes regardless; the notification is retried on its own schedule
    // and goes out once the provider recovers.
    [Fact]
    public async Task EmailProviderDown_SagaStillCompletes_NotificationRetriedUntilSent()
    {
        var sku = await SeedSku(available: 5);
        var email = Cluster.Host(Service.Notifications).Services.GetRequiredService<EmailOptions>();
        email.FailureRate = 1.0;
        try
        {
            var sagaId = await PlaceOrder(sku);
            var saga = await WaitForSaga(sagaId, s => s.Status == SagaStatus.Completed);
            saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));

            // The notification failed and is waiting for its scheduled retry.
            await Eventually(async () => await FailedNotificationAttempts(sagaId) >= 1);
            email.FailureRate = 0;

            await Eventually(async () =>
            {
                await using var db = NotificationsDb();
                return await db.Sent.AnyAsync(n => n.OrderId == sagaId && n.Kind == "completed");
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            email.FailureRate = 0;
        }
    }

    private async Task<int> FailedNotificationAttempts(Guid orderId)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(NotificationsSetup.DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT ISNULL(MAX(attempts), 0) FROM wolverine.wolverine_incoming_envelopes WHERE message_type = 'Contracts.OrderCompleted' AND CAST(body AS varchar(max)) LIKE @id", conn);
        cmd.Parameters.AddWithValue("@id", $"%{orderId:D}%");
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
