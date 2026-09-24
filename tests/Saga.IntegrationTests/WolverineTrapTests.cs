using Contracts;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

// Pins a Wolverine 6.40 behaviour the runbook warns about (docs/runbook.md#traps), so
// an upgrade that changes it is noticed instead of silently making the runbook wrong.
public class WolverineTrapTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: an operator hand-publishes a command from the RabbitMQ UI.
    // With no message type, Wolverine can't handle it and tries to dead-letter it, but
    // dead-lettering throws while building the report, and after 3 attempts the
    // envelope is dropped. No dead-letter row, so no metric and no alert.
    //
    // If this starts failing because the row IS there, Wolverine has fixed it: delete
    // this test and the first bullet under "Traps" in the runbook.
    [Fact]
    public async Task HeaderlessMessage_IsDiscarded_NotDeadLettered_OnWolverine640()
    {
        var sagaId = Guid.CreateVersion7();
        var commandId = CommandId.For(sagaId, StepNames.ReserveStock, Direction.Forward);

        await Cluster.PublishRaw(Queues.Inventory,
            $$"""{"SagaId":"{{sagaId}}","CommandId":"{{commandId}}","Lines":[]}""");
        // PublishRaw has checked RabbitMQ routed it. Quiesce waits for the queue to drain,
        // i.e. for Inventory to consume and ack it. Afterwards it is nowhere: not in the
        // inbox (so not waiting on a scheduled retry either), not in the dead letters.
        await Cluster.Quiesce();

        (await DeadLettersFor(Inventory.InventorySetup.DatabaseName, sagaId)).ShouldBe(0,
            "Wolverine now dead-letters a message with no type header: update docs/runbook.md#traps");
        (await IncomingFor(Inventory.InventorySetup.DatabaseName, sagaId)).ShouldBe(0);
        (await ReservationsFor(sagaId)).ShouldBe(0);
    }
}
