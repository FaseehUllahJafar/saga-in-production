using System.Net;
using System.Net.Http.Json;
using Contracts;
using Orders.Saga;
using Payments.Data;
using Saga.IntegrationTests.Infrastructure;
using Wolverine;
using Wolverine.Attributes;

namespace Saga.IntegrationTests;

// AuthorizePayment as it was before Currency existed, under the wire name of today's
// type. What a message serialized by the previous release looks like to Payments: still
// queued, still in an outbox, or dead-lettered and replayed after the deploy.
[MessageIdentity("Contracts.AuthorizePayment")]
public sealed record AuthorizePaymentBeforeCurrency(Guid SagaId, Guid CommandId, decimal Amount, string CardToken) : ISagaMessage;

// ADR 0004: an additive change, lived through once.
public class MessageEvolutionTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: the deploy that added Currency. Commands the old Orders had
    // already sent were still in the payments queue. A reader that required the field
    // would dead-letter every one of them; a reader that guessed wrong would charge in
    // the wrong currency.
    [Fact]
    public async Task OldShapeAuthorizePayment_WithoutCurrency_IsAuthorizedInDollars()
    {
        var sagaId = Guid.CreateVersion7();
        var commandId = CommandId.For(sagaId, StepNames.AuthorizePayment, Direction.Forward);

        await Bus(Service.Orders).EndpointFor(new Uri($"rabbitmq://queue/{Queues.Payments}"))
            .SendAsync(new AuthorizePaymentBeforeCurrency(sagaId, commandId, 25m, "tok_visa"));

        await Eventually(async () => (await PaymentStepFor(sagaId, StepNames.AuthorizePayment))?.Status == PaymentStepStatus.Authorized);
        var authorization = (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem();
        authorization.Currency.ShouldBe("USD");
        authorization.Amount.ShouldBe(25m);
    }

    [Fact]
    public async Task EuroOrder_IsAuthorizedAndCapturedInEuros()
    {
        var sku = await SeedSku(available: 5);
        var sagaId = Guid.CreateVersion7();

        await Bus(Service.Orders).SendAsync(new StartCheckout(sagaId, $"{sagaId:N}@example.com", "tok_visa", "1 Main St",
            [new OrderLine(Guid.CreateVersion7(), sku, 1, 25m)], "EUR"));
        var saga = await WaitForFinished(sagaId);

        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        saga.Currency.ShouldBe("EUR");
        (await AuthorizationsFor(sagaId)).ShouldHaveSingleItem().Currency.ShouldBe("EUR");
    }

    // Production failure: an authorize whose response was lost just before the deploy,
    // retried just after it under the same idempotency key. If the new field changes the
    // request's hash, the provider refuses the retry as "key reused with a different
    // request", and a charge that did land looks like one that failed.
    [Fact]
    public async Task RetryAcrossTheCurrencyDeploy_SameKey_ReplaysTheFirstAnswer()
    {
        using var http = new HttpClient { BaseAddress = Cluster.FakePayUri };
        var key = Guid.NewGuid().ToString("D");

        var before = await Authorize(http, key, new { amountMinor = 2500, cardToken = "tok_visa" });
        var after = await Authorize(http, key, new { amountMinor = 2500, cardToken = "tok_visa", currency = "USD" });
        var otherCurrency = await Authorize(http, key, new { amountMinor = 2500, cardToken = "tok_visa", currency = "EUR" });

        before.StatusCode.ShouldBe(HttpStatusCode.Created);
        after.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await IdOf(after)).ShouldBe(await IdOf(before));
        // Same key, different meaning: that IS a different request.
        otherCurrency.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private static async Task<HttpResponseMessage> Authorize(HttpClient http, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/authorizations") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await http.SendAsync(request);
    }

    private sealed record AuthorizationId(string Id);

    private static async Task<string> IdOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AuthorizationId>())!.Id;
}
