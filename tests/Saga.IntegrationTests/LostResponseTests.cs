using Contracts;
using FakePay.Data;
using Microsoft.EntityFrameworkCore;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests;

// "A timeout means unknown, not failed." Each test here makes an effect happen at
// FakePay while the answer never reaches Payments, then checks the saga acts on what
// actually happened rather than on the silence.
public class LostResponseTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // The most important test in the repo. Production failure: the charge commits at the
    // processor, the TCP connection dies on the way back, and a naive retry charges the
    // customer twice. Toxiproxy drops FakePay's responses (the requests still get
    // through), so attempt 1 commits and its answer is lost for real.
    [Fact]
    public async Task FakePayResponseLost_RetrySameKey_ExactlyOneAuthorization()
    {
        var sku = await SeedSku(available: 5);
        await Cluster.Toxiproxy.DropResponses(SagaCluster.FakePayProxy);

        var sagaId = await PlaceOrder(sku);
        await Eventually(async () => (await AuthorizationsFor(sagaId)).Count == 1);
        // The request reached FakePay and committed; its response was dropped. Let the
        // Payments client time out for real before the link comes back.
        await Task.Delay(SagaCluster.ProviderTimeout * 2);
        await Cluster.Toxiproxy.RemoveToxic(SagaCluster.FakePayProxy);

        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Captured);
        await using var fakePay = FakePayDb();
        var key = AuthorizeKey(sagaId).ToString("D");
        var record = await fakePay.IdempotencyKeys.SingleAsync(k => k.Key == key && k.Operation == "authorize");
        // Proof the retry happened and was answered from the stored response, not by
        // charging again.
        record.ReplayCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    // Every authorize response is late (tok_slow: FakePay commits, then answers after
    // the client gave up), so every forward attempt times out. The inquiry asks FakePay
    // by idempotency key, finds the charge, and the saga carries on instead of voiding
    // a perfectly good authorization.
    [Fact]
    public async Task AuthorizeTimesOut_ChargeLanded_InquiryFindsIt_ContinuesForward()
    {
        var sku = await SeedSku(available: 5);

        var sagaId = await PlaceOrder(sku, cardToken: "tok_slow");
        var saga = await WaitForFinished(sagaId, TimeSpan.FromSeconds(90));

        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        saga.AuthorizationId.ShouldNotBeNull();
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Captured);
        (await HandledCount(Payments.PaymentsSetup.DatabaseName, "Contracts.CheckStepStatus", sagaId)).ShouldBe(1);
    }

    // The FakePay link is dead through every forward attempt AND the inquiry, so the
    // saga must compensate without an answer. The step stays Unknown, the void still
    // goes out, and when the link recovers the void finds the charge by key and voids it.
    [Fact]
    public async Task InquiryUnanswered_CompensationStillFindsChargeAndVoidsIt()
    {
        var sku = await SeedSku(available: 5);
        await Cluster.Toxiproxy.DropResponses(SagaCluster.FakePayProxy);

        var sagaId = await PlaceOrder(sku);
        await WaitForSaga(sagaId, s => s.Status == SagaStatus.Compensating, TimeSpan.FromSeconds(60));
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Authorized);
        await Cluster.Toxiproxy.RemoveToxic(SagaCluster.FakePayProxy);

        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Cancelled, Describe(saga));
        saga.Journal.Single(j => j.Step == StepNames.AuthorizePayment).Outcome.ShouldBe(StepOutcome.Compensated);
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Status.ShouldBe(AuthorizationStatus.Voided);
        (await StockOf(sku)).ShouldBe(5);
    }
}
