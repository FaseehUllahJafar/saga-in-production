using System.Diagnostics.Metrics;
using Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders;
using Orders.Saga;
using Saga.IntegrationTests.Infrastructure;
using ServiceDefaults;

namespace Saga.IntegrationTests;

// The monitors are the only thing that notices a saga which fails silently. These tests
// seed the states they must detect straight into the tables (a saga whose timeout was
// lost looks exactly like a row that stopped changing) and read what the monitor reports.
public class MonitoringTests(SagaCluster cluster) : IntegrationTest(cluster)
{
    private SagaMonitor SagaMonitor => Cluster.Host(Service.Orders).Services.GetRequiredService<SagaMonitor>();
    private DeadLetterMonitor DeadLetterMonitor(Service service) => Cluster.Host(service).Services.GetRequiredService<DeadLetterMonitor>();

    // Production failure: a saga stops moving and nothing errors. Its timeout was lost,
    // or a handler returned no messages. Only its age gives it away.
    [Fact]
    public async Task SagaWithNoProgress_ReportedAsOldestAndStuck_FinishedSagasIgnored()
    {
        var now = DateTimeOffset.UtcNow;
        var stuck = await SeedSaga(SagaStatus.InProgress, now.AddMinutes(-20), StepNames.ReserveStock);
        var slow = await SeedSaga(SagaStatus.Compensating, now.AddMinutes(-5), StepNames.AuthorizePayment);
        var finished = await SeedSaga(SagaStatus.Completed, now.AddHours(-3));
        try
        {
            var health = await SagaMonitor.PollAsync();

            health.OldestActiveAgeSeconds.ShouldBeInRange(20 * 60 - 5, 20 * 60 + 60);
            health.Active[SagaStatus.InProgress].ShouldBeGreaterThanOrEqualTo(1);
            health.Active[SagaStatus.Compensating].ShouldBeGreaterThanOrEqualTo(1);
            var stuckIds = health.Stuck.Select(s => s.Id).ToList();
            stuckIds.ShouldContain(stuck);
            stuckIds.ShouldNotContain(slow);
            stuckIds.ShouldNotContain(finished);
            health.StuckCount.ShouldBeGreaterThanOrEqualTo(1);
            health.Stuck.Single(s => s.Id == stuck).CurrentStep.ShouldBe(StepNames.ReserveStock);
        }
        finally
        {
            await DeleteSagas(stuck, slow, finished);
        }

        // Once nothing is in flight the gauge must drop to 0, not keep reporting the
        // last stuck saga forever.
        var after = await SagaMonitor.PollAsync();
        after.OldestActiveAgeSeconds.ShouldBe(0);
        after.Stuck.ShouldBeEmpty();
        after.StuckCount.ShouldBe(0);
    }

    // Production failure: a saga parked in CompensationFailed or NeedsManualReview. It is
    // "finished" as far as the saga is concerned, but a human owes it a decision, so it
    // must stay visible until someone resolves it.
    [Fact]
    public async Task ParkedSagas_CountedUntilResolved()
    {
        var before = await SagaMonitor.PollAsync();
        var failed = await SeedSaga(SagaStatus.CompensationFailed, DateTimeOffset.UtcNow.AddHours(-2));
        var review = await SeedSaga(SagaStatus.NeedsManualReview, DateTimeOffset.UtcNow.AddDays(-1));
        try
        {
            var health = await SagaMonitor.PollAsync();
            health.NeedsAttention[SagaStatus.CompensationFailed].ShouldBe(before.NeedsAttention[SagaStatus.CompensationFailed] + 1);
            health.NeedsAttention[SagaStatus.NeedsManualReview].ShouldBe(before.NeedsAttention[SagaStatus.NeedsManualReview] + 1);
            // Parked is not in flight: it must not inflate the stuck-saga numbers.
            health.Stuck.Select(s => s.Id).ShouldNotContain(failed);
            health.Stuck.Select(s => s.Id).ShouldNotContain(review);
        }
        finally
        {
            await DeleteSagas(failed, review);
        }

        (await SagaMonitor.PollAsync()).NeedsAttention.ShouldBe(before.NeedsAttention);
    }

    // The monitor runs every minute on every node against a table that keeps every order
    // ever placed. This pins that its queries read the small filtered indexes and never
    // the table itself; a status passed as a parameter, or a filter that stops matching
    // the query, would silently turn them into full scans.
    [Fact]
    public async Task MonitorQueries_AreServedByTheFilteredIndexes_NeverTheTable()
    {
        var now = DateTimeOffset.UtcNow;
        var seeded = new[]
        {
            await SeedSaga(SagaStatus.InProgress, now.AddMinutes(-30), StepNames.BookShipment),
            await SeedSaga(SagaStatus.CompensationFailed, now.AddMinutes(-30)),
        };
        try
        {
            var before = await IndexUsage();
            await SagaMonitor.PollAsync();
            var after = await IndexUsage();

            (after["IX_CheckoutSagas_Active"] - before["IX_CheckoutSagas_Active"]).ShouldBeGreaterThanOrEqualTo(2);
            (after["IX_CheckoutSagas_NeedsAttention"] - before["IX_CheckoutSagas_NeedsAttention"]).ShouldBeGreaterThanOrEqualTo(1);
            (after["PK_CheckoutSagas"] - before["PK_CheckoutSagas"]).ShouldBe(0);
        }
        finally
        {
            await DeleteSagas(seeded);
        }
    }

    // What an alert rule actually sees: the exported instruments, not the snapshot.
    [Fact]
    public async Task Gauges_ReportTheCachedSnapshot_WithEveryStatusIncludingZeros()
    {
        var stuck = await SeedSaga(SagaStatus.InProgress, DateTimeOffset.UtcNow.AddMinutes(-16), StepNames.CapturePayment);
        try
        {
            var health = await SagaMonitor.PollAsync();
            var observed = ObserveGauges(SagaMetrics.MeterName);

            observed.ShouldContain(m => m.Name == "saga_oldest_active_age_seconds" && m.Value == health.OldestActiveAgeSeconds);
            observed.ShouldContain(m => m.Name == "saga_stuck" && m.Value == health.StuckCount);
            observed.Where(m => m.Name == "saga_active").Select(m => m.Tag("status"))
                .ShouldBe(["InProgress", "Compensating"], ignoreOrder: true);
            observed.Where(m => m.Name == "saga_needs_attention").Select(m => m.Tag("status"))
                .ShouldBe(["CompensationFailed", "NeedsManualReview"], ignoreOrder: true);
            observed.ShouldContain(m => m.Name == "saga_monitor_last_run_timestamp" && m.Tag("monitor") == "sagas"
                && m.Value == health.TakenUtc.ToUnixTimeMilliseconds() / 1000.0);
        }
        finally
        {
            await DeleteSagas(stuck);
        }
    }

    // Production failure: the message that STARTS a saga is dead-lettered. The API has
    // already answered 202, and there is no saga row, so the stuck-saga monitor has
    // nothing to see. The dead-letter age is the only signal. Replaying the dead letter
    // (the runbook's procedure, verbatim) after the fix starts the saga, which completes.
    [Fact]
    public async Task DeadLetteredStart_OnlyTheDeadLetterMonitorSeesIt_RunbookReplayCompletesTheSaga()
    {
        var sku = await SeedSku(available: 3);
        var sagaId = Guid.CreateVersion7();
        FailingSagaCommits.Instance.Arm(sagaId);

        await PlaceOrder(sku, sagaId: sagaId);
        await Eventually(async () => await DeadLettersFor(OrdersSetup.DatabaseName, sagaId) == 1);

        await using (var db = OrdersDb())
        {
            (await db.Sagas.AnyAsync(s => s.Id == sagaId)).ShouldBeFalse();
        }
        (await SagaMonitor.PollAsync()).Stuck.ShouldBeEmpty();
        var deadLetters = await DeadLetterMonitor(Service.Orders).PollAsync();
        deadLetters.Count.ShouldBeGreaterThanOrEqualTo(1);
        // Proves sent_at is populated; the age alert would be blind if it weren't.
        deadLetters.OldestAgeSeconds.ShouldBeGreaterThan(0);

        // The fix ships; then the runbook's replay.
        FailingSagaCommits.Instance.Disarm();
        await Execute(OrdersSetup.DatabaseName,
            "UPDATE wolverine.wolverine_dead_letters SET replayable = 1 WHERE message_type = @type AND CAST(body AS varchar(max)) LIKE @saga",
            ("@type", "Orders.Saga.StartCheckout"), ("@saga", $"%{sagaId:D}%"));

        var saga = await WaitForFinished(sagaId);
        saga.Status.ShouldBe(SagaStatus.Completed, Describe(saga));
        (await DeadLettersFor(OrdersSetup.DatabaseName, sagaId)).ShouldBe(0);
        (await StockOf(sku)).ShouldBe(2);
        (await DeadLetterMonitor(Service.Orders).PollAsync()).Count.ShouldBe(deadLetters.Count - 1);
    }

    // ---- helpers ---------------------------------------------------------------------

    private async Task<Guid> SeedSaga(SagaStatus status, DateTimeOffset lastUpdatedUtc, string currentStep = "")
    {
        var saga = new CheckoutSaga
        {
            Id = Guid.CreateVersion7(),
            CustomerEmail = "monitor@example.com",
            Status = status,
            CurrentStep = currentStep,
            AttemptCount = currentStep.Length > 0 ? 1 : 0,
            StartedUtc = lastUpdatedUtc.AddMinutes(-1),
            LastUpdatedUtc = lastUpdatedUtc,
        };
        await using var db = OrdersDb();
        db.Sagas.Add(saga);
        await db.SaveChangesAsync();
        return saga.Id;
    }

    private async Task DeleteSagas(params Guid[] ids)
    {
        await using var db = OrdersDb();
        await db.Sagas.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync();
    }

    private async Task<Dictionary<string, long>> IndexUsage()
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(OrdersSetup.DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT i.name, COALESCE(u.user_seeks + u.user_scans + u.user_lookups, 0)
            FROM sys.indexes i
            LEFT JOIN sys.dm_db_index_usage_stats u
              ON u.database_id = DB_ID() AND u.object_id = i.object_id AND u.index_id = i.index_id
            WHERE i.object_id = OBJECT_ID('orders.CheckoutSagas') AND i.name IS NOT NULL
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var usage = new Dictionary<string, long>();
        while (await reader.ReadAsync()) usage[reader.GetString(0)] = reader.GetInt64(1);
        return usage;
    }

    private async Task Execute(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(Cluster.SqlConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    // Reads the observable gauges of the Orders host's own meter. Several hosts share this
    // process, so instruments are matched on the meter's scope (the host's IMeterFactory).
    private List<ObservedMeasurement> ObserveGauges(string meterName)
    {
        var scope = Cluster.Host(Service.Orders).Services.GetRequiredService<IMeterFactory>();
        var observed = new List<ObservedMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName && ReferenceEquals(instrument.Meter.Scope, scope)) l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((i, v, tags, _) => observed.Add(new(i.Name, v, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => observed.Add(new(i.Name, v, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();
        return observed;
    }

    private sealed record ObservedMeasurement(string Name, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string key) => Tags.FirstOrDefault(t => t.Key == key).Value?.ToString();
    }
}
