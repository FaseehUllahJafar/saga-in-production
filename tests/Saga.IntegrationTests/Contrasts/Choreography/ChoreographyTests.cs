using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;

namespace Saga.IntegrationTests.Contrasts.Choreography;

// The same checkout without an orchestrator: each service reacts to the previous one's
// event, and each compensation is one more event handler. Kept deliberately small (one
// process, local queues, state in memory) so the SHAPE is what you read. Compare with
// src/Orders/Saga/CheckoutSaga.cs.
//
// What to notice:
// - "What happens when shipping fails?" has no single answer on screen. It is Inventory
//   listening for ShipmentRejected, then Payments listening for StockReleased, then
//   Orders listening for PaymentVoided. Add a step and every neighbour's handlers change.
// - Every event drags along whatever later steps need (Quantity, Address), because no
//   one holds the order.
// - Nobody is waiting. When Shipping goes quiet, no one times out, asks, or undoes
//   anything: the last test below. In the saga that is StepTimeout plus CheckStepStatus.
//
// Choreography is the right call for loosely coupled reactions nobody needs to
// coordinate, which is exactly how Notifications consumes OrderCompleted in this repo.
// It is the wrong call for a multi-step business transaction with compensations.

public sealed record OrderPlaced(Guid OrderId, int Quantity, string Address);
public sealed record PaymentAuthorized(Guid OrderId, int Quantity, string Address);
public sealed record StockReserved(Guid OrderId, int Quantity, string Address);
public sealed record ShipmentBooked(Guid OrderId);
public sealed record ShipmentRejected(Guid OrderId, int Quantity);
public sealed record StockReleased(Guid OrderId);
public sealed record PaymentVoided(Guid OrderId);

// Handlers run on several threads at once, so the shared state has to be safe for that.
public sealed class World
{
    private int _stock = 10;
    public int Stock => _stock;
    public void TakeStock(int quantity) => Interlocked.Add(ref _stock, -quantity);
    public void ReturnStock(int quantity) => Interlocked.Add(ref _stock, quantity);
    public ConcurrentDictionary<Guid, string> Payments { get; } = [];
    public ConcurrentDictionary<Guid, string> Orders { get; } = [];
    public ConcurrentDictionary<Guid, bool> Shipments { get; } = [];
}

public static class ChoreographedOrders
{
    public static void Handle(ShipmentBooked e, World world) => world.Orders[e.OrderId] = "confirmed";
    // The last link of the compensation chain. Orders only learns the order is dead
    // from Payments, two services away from where it failed.
    public static void Handle(PaymentVoided e, World world) => world.Orders[e.OrderId] = "cancelled";
}

public static class ChoreographedPayments
{
    public static PaymentAuthorized Handle(OrderPlaced e, World world)
    {
        world.Payments[e.OrderId] = "authorized";
        return new PaymentAuthorized(e.OrderId, e.Quantity, e.Address);
    }

    // Payments must know that "stock released" means "void the payment". That is
    // Inventory's event carrying Orders' business rule.
    public static PaymentVoided Handle(StockReleased e, World world)
    {
        world.Payments[e.OrderId] = "voided";
        return new PaymentVoided(e.OrderId);
    }
}

public static class ChoreographedInventory
{
    public static StockReserved Handle(PaymentAuthorized e, World world)
    {
        world.TakeStock(e.Quantity);
        return new StockReserved(e.OrderId, e.Quantity, e.Address);
    }

    public static StockReleased Handle(ShipmentRejected e, World world)
    {
        world.ReturnStock(e.Quantity);
        return new StockReleased(e.OrderId);
    }
}

public static class ChoreographedShipping
{
    public static object[] Handle(StockReserved e, World world) => e.Address switch
    {
        "nowhere" => [new ShipmentRejected(e.OrderId, e.Quantity)],
        // A carrier that never answers, and a service that swallows it.
        "silent carrier" => [],
        _ => Book(e, world)
    };

    private static object[] Book(StockReserved e, World world)
    {
        world.Shipments[e.OrderId] = true;
        return [new ShipmentBooked(e.OrderId)];
    }
}

public class ChoreographyTests
{
    [Fact]
    public async Task HappyPath_EventsChainThroughEveryService()
    {
        var (host, world) = await StartHost();
        using var _ = host;
        var orderId = Guid.NewGuid();
        world.Orders[orderId] = "placed";

        await host.SendMessageAndWaitAsync(new OrderPlaced(orderId, 2, "1 Main St"));

        world.Orders[orderId].ShouldBe("confirmed");
        world.Payments[orderId].ShouldBe("authorized");
        world.Stock.ShouldBe(8);
        world.Shipments.ContainsKey(orderId).ShouldBeTrue();
    }

    [Fact]
    public async Task ShipmentRejected_CompensationIsAChainOfEventsAcrossThreeServices()
    {
        var (host, world) = await StartHost();
        using var _ = host;
        var orderId = Guid.NewGuid();
        world.Orders[orderId] = "placed";

        var session = await host.SendMessageAndWaitAsync(new OrderPlaced(orderId, 2, "nowhere"));

        world.Orders[orderId].ShouldBe("cancelled");
        world.Stock.ShouldBe(10);
        world.Payments[orderId].ShouldBe("voided");
        // Four hops of events to undo one failure, each in a different handler.
        session.Executed.MessagesOf<ShipmentRejected>().ShouldHaveSingleItem();
        session.Executed.MessagesOf<StockReleased>().ShouldHaveSingleItem();
        session.Executed.MessagesOf<PaymentVoided>().ShouldHaveSingleItem();
    }

    // The failure choreography can't see: a step that never answers. Every handler
    // finished successfully, nothing was dead-lettered, and the order holds a payment
    // and stock forever. There is no component whose job it is to notice.
    [Fact]
    public async Task ShippingGoesQuiet_NobodyNotices_PaymentAndStockHeldForever()
    {
        var (host, world) = await StartHost();
        using var _ = host;
        var orderId = Guid.NewGuid();
        world.Orders[orderId] = "placed";

        await host.SendMessageAndWaitAsync(new OrderPlaced(orderId, 2, "silent carrier"));

        world.Orders[orderId].ShouldBe("placed");
        world.Payments[orderId].ShouldBe("authorized");
        world.Stock.ShouldBe(8);
    }

    private static async Task<(IHost, World)> StartHost()
    {
        var world = new World();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "choreography-contrast";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ChoreographedOrders))
                    .IncludeType(typeof(ChoreographedPayments))
                    .IncludeType(typeof(ChoreographedInventory))
                    .IncludeType(typeof(ChoreographedShipping));
                opts.Services.AddSingleton(world);
            })
            .StartAsync();
        return (host, world);
    }
}
