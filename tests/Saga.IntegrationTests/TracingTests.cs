using System.Collections.Concurrent;
using System.Diagnostics;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;
using ServiceDefaults;

namespace Saga.IntegrationTests;

public class TracingTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    // Production failure: "what happened to order X?" answered by grepping five services'
    // logs for a GUID that half the lines don't contain. With saga.id on every span, one
    // dashboard filter shows the whole saga, including FakePay's side of each call.
    [Fact]
    public async Task SagaId_TagsEveryHandlerSpan_InEveryService_AndFakePaysRequests()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Wolverine" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var sku = await SeedSku(available: 2);
        var sagaId = await PlaceOrder(sku);
        var saga = await WaitForFinished(sagaId);
        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));

        // A span stops just after its envelope is marked handled, so the last one
        // (Notifications' OrderCompleted) can trail the quiet cluster by a moment.
        string[] expected =
        [
            "Orders.Saga.StartCheckout",
            "Contracts.AuthorizePayment", "Contracts.PaymentAuthorized",
            "Contracts.ReserveStock", "Contracts.StockReserved",
            "Contracts.BookShipment", "Contracts.ShipmentBooked",
            "Contracts.CapturePayment", "Contracts.PaymentCaptured",
            "Contracts.OrderCompleted",
        ];
        List<Activity> tagged = [];
        HashSet<string?> handled = [];
        // Orchestrator, each participant, and Notifications, which is outside the saga.
        await Eventually(() =>
        {
            tagged = spans.Where(a => a.GetTagItem(SagaTracing.TagName) as string == sagaId.ToString("D")).ToList();
            handled = tagged.Where(a => a.Source.Name == "Wolverine")
                .Select(a => a.GetTagItem("messaging.message_type") as string)
                .ToHashSet();
            return Task.FromResult(expected.All(handled.Contains));
        }, TimeSpan.FromSeconds(10), () => $"missing: {string.Join(", ", expected.Where(e => !handled.Contains(e)))}");

        // FakePay's request spans: authorize and capture, tagged from the X-Saga-Id header.
        tagged.Count(a => a.Source.Name == "Microsoft.AspNetCore").ShouldBeGreaterThanOrEqualTo(2);
    }

    // Log lines that don't mention the SagaId in their text still carry it as a scope:
    // here, HttpClient's own lines for the calls Payments makes to FakePay.
    [Fact]
    public async Task SagaId_IsInScope_ForLogLinesThatDontMentionIt()
    {
        var sku = await SeedSku(available: 2);
        var sagaId = await PlaceOrder(sku);
        (await WaitForFinished(sagaId)).Status.ShouldBe(SagaStatus.Completed);

        ScopeRecorder.Entries
            .Where(e => e.Category.StartsWith("System.Net.Http.HttpClient.fakepay"))
            .Count(e => e.Scope.TryGetValue(SagaTracing.ScopeKey, out var id) && (string?)id == sagaId.ToString("D"))
            .ShouldBeGreaterThanOrEqualTo(2);
    }
}
