using System.Net;
using System.Net.Http.Json;
using FakePay.Data;
using Microsoft.EntityFrameworkCore;
using Saga.IntegrationTests.Infrastructure;
using Authorization = FakePay.Data.Authorization;

namespace Saga.IntegrationTests;

// The operator's cancel of a saga parked at capture voids FIRST, on the promise that a
// capture still in flight somewhere will then be refused. That promise is the provider's:
// of a void and a capture racing on one authorization, exactly one may win.
public class ProviderRaceTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: the void lands while a capture is between reading the
    // authorization and writing it. If both answer "done", Payments records a void for an
    // order whose money was taken, and the saga cancels a paid order.
    // (tok_slow_capture holds the first capture inside FakePay after its read.)
    [Fact]
    public async Task VoidLandsWhileCaptureIsInFlight_CaptureIsRefused_MoneyNotTaken()
    {
        using var http = new HttpClient { BaseAddress = Cluster.FakePayUri };
        var authorizationId = await Authorize(http, "tok_slow_capture");

        var capture = Post(http, $"/v1/authorizations/{authorizationId}/capture", new { amountMinor = 3000 }, Guid.NewGuid().ToString("D"));
        // The capture has read the row as Authorized and is holding before its write.
        await Eventually(async () => (await Row(authorizationId)).CaptureAttempts == 0);

        using var voided = await Post(http, $"/v1/authorizations/{authorizationId}/void", new { }, idempotencyKey: null);
        using var captured = await capture;

        voided.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await captured.Content.ReadAsStringAsync()).ShouldContain("authorization_voided");
        var row = await Row(authorizationId);
        row.Status.ShouldBe(AuthorizationStatus.Voided);
        row.CaptureId.ShouldBeNull();
    }

    private static async Task<string> Authorize(HttpClient http, string cardToken)
    {
        using var response = await Post(http, "/v1/authorizations", new { amountMinor = 3000, cardToken }, Guid.NewGuid().ToString("D"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AuthorizationId>())!.Id;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string path, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return http.SendAsync(request);
    }

    private async Task<Authorization> Row(string id)
    {
        await using var db = FakePayDb();
        return await db.Authorizations.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private sealed record AuthorizationId(string Id);
}
