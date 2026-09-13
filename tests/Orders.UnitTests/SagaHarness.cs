using System.Diagnostics.Metrics;
using Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orders.Saga;
using Wolverine;

namespace Orders.UnitTests;

// Drives CheckoutSaga's handlers directly: no host, no broker, no database. What these
// tests pin down is the transition table, the journal and the gating rules.
internal sealed class SagaHarness
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

    public SagaTimings Timings { get; } = new()
    {
        ForwardAttemptTimeouts = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)],
        InquiryTimeout = TimeSpan.FromMinutes(1),
        CompensationAttemptTimeouts = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)],
    };

    public SagaRuntime Runtime { get; }

    public SagaHarness()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        Runtime = new SagaRuntime(Clock, Timings, new SagaMetrics(meterFactory), NullLogger<CheckoutSaga>.Instance);
    }

    public CheckoutSaga Saga { get; private set; } = null!;

    public OutgoingMessages Start(string cardToken = "tok_visa")
    {
        var (saga, messages) = CheckoutSaga.Start(new StartCheckout(
            Guid.CreateVersion7(),
            "ada@example.com",
            cardToken,
            "1 Main St",
            [new OrderLine(Guid.CreateVersion7(), "BOOK-DDD", 2, 30m)]), Runtime);
        Saga = saga;
        return messages;
    }

    public Guid Fwd(string step) => CommandId.For(Saga.Id, step, Direction.Forward);
    public Guid Comp(string step) => CommandId.For(Saga.Id, step, Direction.Compensate);

    public StepTimeout Timeout(string step, int attempt, Direction direction = Direction.Forward) =>
        new(Saga.Id, step, attempt, direction, TimeSpan.Zero);

    // Happy path up to (and not including) the reply for `stopAt`.
    public void AdvanceTo(string stopAt)
    {
        Start();
        if (stopAt == StepNames.AuthorizePayment) return;
        Saga.Handle(new PaymentAuthorized(Saga.Id, Fwd(StepNames.AuthorizePayment), "auth_1"), Runtime);
        if (stopAt == StepNames.ReserveStock) return;
        Saga.Handle(new StockReserved(Saga.Id, Fwd(StepNames.ReserveStock)), Runtime);
        if (stopAt == StepNames.BookShipment) return;
        Saga.Handle(new ShipmentBooked(Saga.Id, Fwd(StepNames.BookShipment), "TRK1"), Runtime);
    }
}

internal static class OutgoingExtensions
{
    // Unwraps ToEndpoint()/DelayedFor() envelopes so assertions see the message itself.
    public static IReadOnlyList<object> Unwrapped(this OutgoingMessages messages) =>
        messages.Select(m => m.GetType().GetProperty("Message")?.GetValue(m) ?? m).ToList();

    public static T Single<T>(this OutgoingMessages messages) => messages.Unwrapped().OfType<T>().ShouldHaveSingleItem();
}
