using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Saga.E2E;

// One test through the real AppHost: the same containers, projects and wiring that
// `aspire run` starts, driven only through the public HTTP API. If this passes, the
// README's quick start works.
public class AppHostSmokeTests
{
    private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task PlaceOrder_ThroughTheRealAppHost_Completes_AndRetriesAreIdempotent()
    {
        using var cts = new CancellationTokenSource(StartupBudget + TimeSpan.FromMinutes(2));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(cts.Token);
        await using var app = await builder.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);
        foreach (var service in new[] { "orders", "payments", "inventory", "shipping", "notifications", "fakepay" })
        {
            await app.ResourceNotifications.WaitForResourceAsync(service, KnownResourceStates.Running, cts.Token);
        }
        await app.ResourceNotifications.WaitForResourceHealthyAsync("orders", cts.Token);

        using var http = app.CreateHttpClient("orders");
        var order = new
        {
            customerEmail = "e2e@example.com",
            cardToken = "tok_visa",
            shippingAddress = "1 Main St",
            lines = new[] { new { sku = "BOOK-DDD", quantity = 1, unitPrice = 30m } }
        };

        // A double-clicked checkout: three identical requests with one key, at once.
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(order) };
            request.Headers.Add("Idempotency-Key", "e2e-checkout-1");
            return http.SendAsync(request, cts.Token);
        }));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted);
        var ids = await Task.WhenAll(responses.Select(async r =>
            (await r.Content.ReadFromJsonAsync<JsonElement>(cts.Token)).GetProperty("orderId").GetGuid()));
        ids.Distinct().ShouldHaveSingleItem();

        string status = "";
        while (status is not ("Completed" or "Cancelled" or "CompensationFailed" or "NeedsManualReview"))
        {
            await Task.Delay(500, cts.Token);
            // 404 until the saga's first step has committed; accepted is not started.
            using var current = await http.GetAsync($"/orders/{ids[0]}", cts.Token);
            if (current.StatusCode == HttpStatusCode.NotFound) continue;
            status = (await current.Content.ReadFromJsonAsync<JsonElement>(cts.Token)).GetProperty("status").GetString()!;
        }

        status.ShouldBe("Completed");
    }
}
