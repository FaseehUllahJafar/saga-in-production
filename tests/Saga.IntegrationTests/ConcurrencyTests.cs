using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

public class ConcurrencyTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: Orders is scaled out, and a reply and the saga's own timeout
    // for the same step are handled at the same moment on DIFFERENT nodes. Both load the
    // same row; without optimistic concurrency the saga would both advance AND resend, or
    // silently lose one decision. The interceptor holds each commit open so the overlap
    // is certain rather than lucky.
    [Fact]
    public async Task ReplyRacesTimeout_RowVersionConflict_LoserReEvaluates_SagaCompletesOnce()
    {
        var sku = await SeedSku(available: 5);
        await Cluster.Stop(Service.Payments);

        var sagaId = await PlaceOrder(sku);
        await WaitForSaga(sagaId, s => s.CurrentStep == StepNames.AuthorizePayment && s.AttemptCount == 1);
        // Long enough to cover the scheduled-message poll that delivers the timeout.
        SlowSagaCommits.Instance.Arm(sagaId, TimeSpan.FromMilliseconds(2500));
        SagaConflictRecorder.Instance.Reset();

        // A reply and a timeout for the same attempt, released at the same instant onto
        // two Orders nodes. Payments stays down so nothing else answers. (The saga's own
        // attempt-1 timeout is still scheduled and may join in; it only ever adds a
        // stale discard or another conflict, never a second advance.)
        var reply = new PaymentAuthorized(sagaId, AuthorizeKey(sagaId), "auth_from_race");
        var timeout = new StepTimeout(sagaId, StepNames.AuthorizePayment, 1, Direction.Forward, TimeSpan.Zero);
        var secondNode = await Cluster.StartSecondOrdersNode();
        var secondBus = secondNode.Services.CreateScope().ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
        await Task.WhenAll(Bus(Service.Orders).SendAsync(reply).AsTask(), secondBus.SendAsync(timeout).AsTask());

        // Whichever lost the commit re-ran against fresh state: the saga has advanced to
        // reserve exactly once, whether the timeout ran first (attempt 2) or not.
        await WaitForSaga(sagaId, s => s.CurrentStep != StepNames.AuthorizePayment, TimeSpan.FromSeconds(30));
        SlowSagaCommits.Instance.Disarm();
        await Cluster.Start(Service.Payments);
        var saga = await WaitForFinished(sagaId, TimeSpan.FromSeconds(60));

        SagaConflictRecorder.Instance.Conflicts.ShouldBeGreaterThan(0,
            $"the test must actually produce a rowversion conflict (commits held: {SlowSagaCommits.Instance.Delayed})");
        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
        (await ReservationsFor(sagaId)).ShouldBe(1);
        // The loser was retried and re-evaluated, not parked as a poison message.
        (await DeadLettersFor(Orders.OrdersSetup.DatabaseName, sagaId)).ShouldBe(0);
    }

    // Production failure: a flash sale. Ten orders race for the last unit; overselling
    // means shipping something that doesn't exist, and negative stock means the check
    // and the decrement weren't atomic.
    [Fact]
    public async Task LastUnit_TenConcurrentOrders_ExactlyOneCompletes_StockNeverNegative()
    {
        var sku = await SeedSku(available: 1);

        var sagaIds = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PlaceOrder(sku)));
        foreach (var id in sagaIds)
        {
            await WaitForSaga(id, s => s.Status is SagaStatus.Completed or SagaStatus.Cancelled, TimeSpan.FromSeconds(90));
        }
        await Cluster.Quiesce();

        await using var db = OrdersDb();
        var outcomes = await db.Sagas.AsNoTracking().Where(s => sagaIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Status);
        outcomes.Values.Count(s => s == SagaStatus.Completed).ShouldBe(1);
        outcomes.Values.Count(s => s == SagaStatus.Cancelled).ShouldBe(9);
        (await StockOf(sku)).ShouldBe(0);

        // Every loser's authorization was voided: nobody is left with a hold on their card.
        foreach (var (id, status) in outcomes)
        {
            var expected = status == SagaStatus.Completed
                ? FakePay.Data.AuthorizationStatus.Captured
                : FakePay.Data.AuthorizationStatus.Voided;
            (await AuthorizationsFor(id)).ShouldHaveSingleItem().Status.ShouldBe(expected);
        }
    }
}
