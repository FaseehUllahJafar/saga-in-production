using Microsoft.Data.SqlClient;
using Saga.IntegrationTests.Infrastructure;

namespace Saga.IntegrationTests.Contrasts.NotASaga;

// The same three steps with no saga at all: one database, one transaction. If the
// shipment can't be booked, ROLLBACK, and the payment hold and the stock reservation
// never existed. No journal, no compensations, no tombstones, no timeouts, no inquiry,
// no dead letters, no runbook. One round trip to one database instead of a dozen
// messages across four services and a provider.
//
// This is the right call far more often than sagas are: whenever the steps' data can
// live in one database that one team owns. A saga buys independent deployment and
// ownership, and pays for it with everything in src/Orders/Saga.
//
// Where it stops working: the moment one step is someone else's system. A real card
// processor can't join this transaction, so "authorize" here is only a row. Put a
// FakePay call inside it and a rollback can't take the authorization back, which is
// the problem the rest of this repo exists to solve.
public class NotASagaTests(SagaCluster cluster) : IAsyncLifetime
{
    private const string Database = "notasaga";

    [Fact]
    public async Task ShipmentFails_RollbackUndoesEverything_NothingToCompensate()
    {
        var orderId = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(() => Checkout(orderId, quantity: 2, address: "nowhere"));

        (await Scalar("SELECT Available FROM Stock WHERE Sku = 'BOOK-DDD'")).ShouldBe(10);
        (await Scalar($"SELECT COUNT(*) FROM PaymentHolds WHERE OrderId = '{orderId}'")).ShouldBe(0);
        (await Scalar($"SELECT COUNT(*) FROM Shipments WHERE OrderId = '{orderId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task HappyPath_AllThreeCommitTogether()
    {
        var orderId = Guid.NewGuid();

        await Checkout(orderId, quantity: 2, address: "1 Main St");

        (await Scalar("SELECT Available FROM Stock WHERE Sku = 'BOOK-DDD'")).ShouldBe(8);
        (await Scalar($"SELECT COUNT(*) FROM PaymentHolds WHERE OrderId = '{orderId}'")).ShouldBe(1);
        (await Scalar($"SELECT COUNT(*) FROM Shipments WHERE OrderId = '{orderId}'")).ShouldBe(1);
    }

    private async Task Checkout(Guid orderId, int quantity, string address)
    {
        await using var conn = await Open();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        await Execute(conn, tx, "INSERT INTO PaymentHolds (OrderId, Status) VALUES (@id, 'authorized')", orderId);

        // The same conditional UPDATE as src/Inventory: no read-then-write race.
        var reserved = await Execute(conn, tx, $"UPDATE Stock SET Available = Available - {quantity} WHERE Sku = 'BOOK-DDD' AND Available >= {quantity}", orderId);
        if (reserved == 0) throw new InvalidOperationException("insufficient stock");

        if (address == "nowhere") throw new InvalidOperationException("carrier rejected the address");
        await Execute(conn, tx, "INSERT INTO Shipments (OrderId) VALUES (@id)", orderId);

        await tx.CommitAsync();
        // Any exception above disposes the transaction uncommitted: that is the rollback.
    }

    public async ValueTask InitializeAsync()
    {
        await using (var master = new SqlConnection(cluster.SqlConnectionString("master")))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"IF DB_ID('{Database}') IS NULL CREATE DATABASE [{Database}]", master);
            await create.ExecuteNonQueryAsync();
        }

        await using var conn = await Open();
        await using var schema = new SqlCommand("""
            IF OBJECT_ID('Stock') IS NOT NULL DROP TABLE Stock;
            IF OBJECT_ID('PaymentHolds') IS NOT NULL DROP TABLE PaymentHolds;
            IF OBJECT_ID('Shipments') IS NOT NULL DROP TABLE Shipments;
            CREATE TABLE Stock (Sku nvarchar(32) PRIMARY KEY, Available int NOT NULL);
            CREATE TABLE PaymentHolds (OrderId uniqueidentifier PRIMARY KEY, Status nvarchar(16) NOT NULL);
            CREATE TABLE Shipments (OrderId uniqueidentifier PRIMARY KEY);
            INSERT INTO Stock VALUES ('BOOK-DDD', 10);
            """, conn);
        await schema.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SqlConnection> Open()
    {
        var conn = new SqlConnection(cluster.SqlConnectionString(Database));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<int> Execute(SqlConnection conn, SqlTransaction tx, string sql, Guid orderId)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", orderId);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> Scalar(string sql)
    {
        await using var conn = await Open();
        await using var cmd = new SqlCommand(sql, conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
