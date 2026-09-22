using System.Net.Http.Json;
using Contracts;
using Microsoft.Data.SqlClient;
using Orders;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

// The restart tests elsewhere stop hosts politely, and a polite stop hands the node's
// envelopes back before it goes. A crash hands nothing back. These tests kill a real
// Orders process and check the rest of the cluster takes over what it owned.
public class CrashTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: an Orders node is killed (OOM, host failure) while its outbox
    // holds a command the broker never received. The rows still name the dead node as
    // their owner, and so does the saga's scheduled timeout: no live node will touch
    // either until one notices the owner's heartbeat has stopped (StaleNodeTimeout, 60 s
    // by default) and takes them over. Until then the saga is silently frozen.
    // Checked the other way round: with StaleNodeTimeout at an hour, this test fails with
    // the saga still at authorize-payment attempt 1. Not even its timeout fired.
    [Fact]
    public async Task OrdersNodeKilled_WithCommandsInItsOutbox_SurvivorTakesThemOver_SagaCompletes()
    {
        var sku = await SeedSku(available: 3);

        // The only Orders node is the one about to die, so it owns everything.
        await Cluster.Stop(Service.Orders);
        await using var doomed = await OrdersProcess.Start(Cluster.OrdersProcessEnvironment());

        // Broker unreachable: the saga starts and commits, and its first command waits
        // in the outbox for a broker that isn't there.
        await Cluster.Toxiproxy.SetEnabled(SagaCluster.RabbitProxy, false);
        var sagaId = await PlaceOrderThrough(doomed.BaseAddress, sku);
        await WaitForSaga(sagaId, s => s.Status == SagaStatus.InProgress && s.CurrentStep == StepNames.AuthorizePayment);

        List<int> owners = [];
        await Eventually(async () => (owners = await OutboxOwners(sagaId)).Count > 0);
        var deadNode = owners.Distinct().ShouldHaveSingleItem();
        deadNode.ShouldBeGreaterThan(0, "the outbox rows must belong to the node, not be up for grabs already");

        doomed.Kill();
        await Cluster.Toxiproxy.SetEnabled(SagaCluster.RabbitProxy, true);
        await Cluster.Start(Service.Orders);

        var saga = await WaitForFinished(sagaId, TimeSpan.FromMinutes(2));
        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
        (await OutboxOwners(sagaId)).ShouldBeEmpty();
        (await EnvelopesOwnedBy(deadNode)).ShouldBe(0);
    }

    private static async Task<Guid> PlaceOrderThrough(Uri orders, string sku)
    {
        using var http = new HttpClient { BaseAddress = orders };
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                customerEmail = "crash@example.com",
                cardToken = "tok_visa",
                shippingAddress = "1 Main St",
                lines = new[] { new { sku, quantity = 1, unitPrice = 25m } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlacedOrder>())!.OrderId;
    }

    private sealed record PlacedOrder(Guid OrderId);

    private async Task<List<int>> OutboxOwners(Guid sagaId)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(OrdersSetup.DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT owner_id FROM wolverine.wolverine_outgoing_envelopes WHERE CAST(body AS varchar(max)) LIKE @saga", conn);
        cmd.Parameters.AddWithValue("@saga", $"%{sagaId:D}%");
        await using var reader = await cmd.ExecuteReaderAsync();
        var owners = new List<int>();
        while (await reader.ReadAsync()) owners.Add(reader.GetInt32(0));
        return owners;
    }

    private async Task<int> EnvelopesOwnedBy(int node)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(OrdersSetup.DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT (SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes WHERE owner_id = @node)
                 + (SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE owner_id = @node AND status <> 'Handled')
            """, conn);
        cmd.Parameters.AddWithValue("@node", node);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
