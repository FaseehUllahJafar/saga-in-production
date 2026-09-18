using Contracts;
using FakePay;
using FakePay.Data;
using Inventory;
using Inventory.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications;
using Notifications.Data;
using Orders;
using Orders.Data;
using Orders.Saga;
using Payments;
using Payments.Data;
using Shipping;
using Shipping.Data;
using Wolverine;

namespace Saga.IntegrationTests.Infrastructure;

// Every test starts from a healthy cluster (all services up, proxies clean) and isolates
// itself by SagaId and by SKUs it seeds for itself. Assertions read outcomes from the
// services' own databases, never from logs or message traces.
public abstract class IntegrationTest(SagaCluster cluster) : IAsyncLifetime
{
    protected static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(60);

    protected SagaCluster Cluster { get; } = cluster;

    public virtual async ValueTask InitializeAsync() => await Cluster.EnsureAllRunning();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- arrange -------------------------------------------------------------------

    protected async Task<string> SeedSku(int available)
    {
        var sku = $"T-{Guid.NewGuid():N}"[..24];
        await using var db = InventoryDb();
        db.Stock.Add(new StockItem { Sku = sku, Available = available });
        await db.SaveChangesAsync();
        return sku;
    }

    protected async Task<Guid> PlaceOrder(string sku, int quantity = 1, string cardToken = "tok_visa", string address = "1 Main St", Guid? sagaId = null)
    {
        var id = sagaId ?? Guid.CreateVersion7();
        var command = new StartCheckout(id, $"{id:N}@example.com", cardToken, address,
            [new OrderLine(Guid.CreateVersion7(), sku, quantity, 25m)]);
        await Bus(Service.Orders).SendAsync(command);
        return id;
    }

    // Sends a raw participant command through the real broker, exactly as the saga would.
    protected Task SendFromOrders(object message) => Bus(Service.Orders).SendAsync(message).AsTask();

    protected IMessageBus Bus(Service service) =>
        Cluster.Host(service).Services.CreateScope().ServiceProvider.GetRequiredService<IMessageBus>();

    // ---- wait ----------------------------------------------------------------------

    protected async Task<CheckoutSaga> WaitForSaga(Guid sagaId, Func<CheckoutSaga, bool> condition, TimeSpan? deadline = null)
    {
        CheckoutSaga? last = null;
        await Eventually(async () =>
        {
            await using var db = OrdersDb();
            last = await db.Sagas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sagaId);
            return last is not null && condition(last);
        }, deadline, () => last is null
            ? $"saga {sagaId} never appeared"
            : $"saga {sagaId} stuck at {last.Status}/{last.CurrentStep} attempt {last.AttemptCount}, reason '{last.FailureReason}', journal {string.Join(", ", last.Journal.Select(j => $"{j.Step}={j.Outcome}"))}");
        return last!;
    }

    // A terminal saga state comes BEFORE the participants finish: a late void, a
    // release, the notification. Waiting for the whole cluster to go quiet means the
    // side-effect assertions that follow see the final picture, not a race.
    protected async Task<CheckoutSaga> WaitForFinished(Guid sagaId, TimeSpan? deadline = null)
    {
        await WaitForSaga(sagaId, s => s.Status is SagaStatus.Completed or SagaStatus.Cancelled or SagaStatus.CompensationFailed or SagaStatus.NeedsManualReview, deadline);
        await Cluster.Quiesce();
        return await WaitForSaga(sagaId, _ => true);
    }

    protected static string Describe(CheckoutSaga s) =>
        $"{s.Status}/{s.CurrentStep} attempt {s.AttemptCount}, reason '{s.FailureReason}', journal {string.Join(", ", s.Journal.Select(j => $"{j.Step}={j.Outcome}"))}";

    protected static async Task Eventually(Func<Task<bool>> condition, TimeSpan? deadline = null, Func<string>? describe = null)
    {
        var until = DateTime.UtcNow + (deadline ?? DefaultDeadline);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException(describe?.Invoke() ?? "condition not met before the deadline");
    }

    // ---- read outcomes -------------------------------------------------------------

    protected static Guid AuthorizeKey(Guid sagaId) => CommandId.For(sagaId, StepNames.AuthorizePayment, Direction.Forward);

    protected async Task<List<Authorization>> AuthorizationsFor(Guid sagaId)
    {
        await using var db = FakePayDb();
        var key = AuthorizeKey(sagaId).ToString("D");
        var ids = await db.IdempotencyKeys.Where(k => k.Key == key && k.Operation == "authorize" && k.AuthorizationId != null)
            .Select(k => k.AuthorizationId).ToListAsync();
        return await db.Authorizations.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync();
    }

    protected async Task<int> StockOf(string sku)
    {
        await using var db = InventoryDb();
        return await db.Stock.Where(s => s.Sku == sku).Select(s => s.Available).SingleAsync();
    }

    protected async Task<int> ReservationsFor(Guid sagaId)
    {
        await using var db = InventoryDb();
        return await db.Reservations.CountAsync(r => r.SagaId == sagaId);
    }

    protected async Task<Shipment?> ShipmentFor(Guid sagaId)
    {
        await using var db = ShippingDb();
        return await db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.SagaId == sagaId);
    }

    protected async Task<PaymentStep?> PaymentStepFor(Guid sagaId, string step)
    {
        await using var db = PaymentsDb();
        return await db.Steps.AsNoTracking().FirstOrDefaultAsync(s => s.SagaId == sagaId && s.Step == step);
    }

    // Envelope queries are scoped to one saga by searching the stored JSON body for its
    // id, so rows left behind by earlier tests never leak into an assertion.
    protected Task<int> HandledCount(string database, string messageType, Guid sagaId) => Scalar(database,
        "SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE message_type = @type AND status = 'Handled' AND CAST(body AS varchar(max)) LIKE @saga",
        ("@type", messageType), ("@saga", $"%{sagaId:D}%"));

    protected Task<int> DeadLettersFor(string database, Guid sagaId) => Scalar(database,
        "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters WHERE CAST(body AS varchar(max)) LIKE @saga",
        ("@saga", $"%{sagaId:D}%"));

    private async Task<int> Scalar(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    protected OrdersDbContext OrdersDb() => Cluster.Db<OrdersDbContext>(OrdersSetup.DatabaseName, o => new(o));
    protected PaymentsDbContext PaymentsDb() => Cluster.Db<PaymentsDbContext>(PaymentsSetup.DatabaseName, o => new(o));
    protected InventoryDbContext InventoryDb() => Cluster.Db<InventoryDbContext>(InventorySetup.DatabaseName, o => new(o));
    protected ShippingDbContext ShippingDb() => Cluster.Db<ShippingDbContext>(ShippingSetup.DatabaseName, o => new(o));
    protected NotificationsDbContext NotificationsDb() => Cluster.Db<NotificationsDbContext>(NotificationsSetup.DatabaseName, o => new(o));
    protected FakePayDbContext FakePayDb() => Cluster.Db<FakePayDbContext>(FakePaySetup.DatabaseName, o => new(o));
}
