using Contracts;
using FakePay.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Orders;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;
using Shipping;

namespace Saga.IntegrationTests;

// Infrastructure going away while sagas are in flight: the broker, the orchestrator,
// a participant in the middle of a provider call.
public class OutageTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: RabbitMQ is unreachable at the moment the saga commits its
    // first step. Without an outbox the state change commits and the command is lost,
    // or the command goes out and the state change rolls back. With it, both wait in
    // SQL and the command drains when the broker is back.
    [Fact]
    public async Task BrokerDownDuringCommit_StatePersists_MessageDrainsLater()
    {
        var sku = await SeedSku(available: 5);
        await Cluster.Toxiproxy.SetEnabled(SagaCluster.RabbitProxy, false);

        // StartCheckout travels on Orders' durable LOCAL queue, not the broker, so the
        // saga starts even though RabbitMQ can't be reached.
        var sagaId = await PlaceOrder(sku);
        await WaitForSaga(sagaId, s => s.CurrentStep == StepNames.AuthorizePayment);
        await Eventually(async () => await OutgoingEnvelopes(OrdersSetup.DatabaseName, "Contracts.AuthorizePayment", sagaId) >= 1);
        (await AuthorizationsFor(sagaId)).ShouldBeEmpty();

        await Cluster.Toxiproxy.SetEnabled(SagaCluster.RabbitProxy, true);
        var saga = await WaitForFinished(sagaId, TimeSpan.FromSeconds(90));

        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
    }

    // Production failure: the orchestrator restarts while a step is waiting on a
    // participant that swallowed the command (the carrier hung, Shipping said nothing).
    // Only the saga's own timeout can move it on, and that timeout lives in the Orders
    // database, not in memory. (This is a graceful stop: Wolverine releases the node's
    // envelopes on shutdown. A hard kill is recovered by the durability agent's node
    // reassignment instead, which this suite does not exercise.)
    [Fact]
    public async Task OrchestratorRestart_ScheduledTimeoutSurvives_SagaCompletes()
    {
        var sku = await SeedSku(available: 5);
        var carrier = Cluster.Host(Service.Shipping).Services.GetRequiredService<CarrierOptions>();
        var carrierClient = Cluster.Host(Service.Shipping).Services.GetRequiredService<CarrierClient>();
        carrier.TimeoutRate = 1.0;
        try
        {
            var sagaId = await PlaceOrder(sku);
            var bookKey = CommandId.For(sagaId, StepNames.BookShipment, Direction.Forward);
            await Eventually(() => Task.FromResult(carrierClient.BookAttempts(bookKey) >= 1));
            await WaitForSaga(sagaId, s => s.CurrentStep == StepNames.BookShipment && s.AttemptCount == 1);

            await Cluster.Stop(Service.Orders);
            carrier.TimeoutRate = 0;
            (await ScheduledTimeouts(sagaId)).ShouldBeGreaterThanOrEqualTo(1);
            await Cluster.Start(Service.Orders);

            var saga = await WaitForFinished(sagaId, TimeSpan.FromSeconds(60));

            saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
            // The first booking attempt was swallowed; only a re-send driven by the
            // persisted timeout could have produced the second.
            carrierClient.BookAttempts(bookKey).ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            carrier.TimeoutRate = 0;
        }
    }

    // Production failure: Payments is shut down while its authorize request is inside
    // FakePay. FakePay still commits. The command was never acknowledged, so it is
    // redelivered on restart, finds its Pending row, and asks FakePay again with the
    // same key: one authorization, and the saga finishes.
    [Fact]
    public async Task PaymentsStoppedMidProviderCall_RedeliveredOnRestart_ExactlyOneAuthorization()
    {
        var sku = await SeedSku(available: 5);

        var sagaId = await PlaceOrder(sku, cardToken: "tok_slow_commit");
        await Eventually(async () => (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))?.LastProviderCallUtc is not null);
        await Cluster.Stop(Service.Payments);
        await Eventually(async () => (await AuthorizationsFor(sagaId)).Count == 1);
        await Cluster.Start(Service.Payments);

        var saga = await WaitForFinished(sagaId, TimeSpan.FromSeconds(90));

        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
        // Whichever way the saga ended, the one authorization must not be left live.
        if (saga.Status == SagaStatus.Completed)
        {
            (await AuthorizationsFor(sagaId)).Single().Status.ShouldBe(AuthorizationStatus.Captured);
        }
        else
        {
            saga.Status.ShouldBe(SagaStatus.Cancelled, Describe(saga));
            (await AuthorizationsFor(sagaId)).Single().Status.ShouldBe(AuthorizationStatus.Voided);
        }
    }

    private async Task<int> OutgoingEnvelopes(string database, string messageType, Guid sagaId)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes WHERE message_type = @type AND CAST(body AS varchar(max)) LIKE @saga", conn);
        cmd.Parameters.AddWithValue("@type", messageType);
        cmd.Parameters.AddWithValue("@saga", $"%{sagaId:D}%");
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> ScheduledTimeouts(Guid sagaId)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(OrdersSetup.DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type = @type AND CAST(body AS varchar(max)) LIKE @saga", conn);
        cmd.Parameters.AddWithValue("@type", typeof(StepTimeout).FullName);
        cmd.Parameters.AddWithValue("@saga", $"%{sagaId:D}%");
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
