using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Data;

namespace Orders.Saga;

public sealed class SagaMonitorOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    // No progress for this long and a saga counts as stuck. Every waiting state in the
    // saga moves on well inside it: the longest single wait is an 8 minute compensation
    // attempt, and every attempt touches LastUpdatedUtc.
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record StuckSaga(Guid Id, string Status, string CurrentStep, int AttemptCount, DateTimeOffset LastUpdatedUtc);

public sealed record SagaHealth(
    DateTimeOffset TakenUtc,
    IReadOnlyDictionary<SagaStatus, int> Active,
    IReadOnlyDictionary<SagaStatus, int> NeedsAttention,
    double OldestActiveAgeSeconds,
    int StuckCount,
    // The oldest few, for the log line and the runbook. StuckCount is the real total.
    IReadOnlyList<StuckSaga> Stuck);

// Part 5's stuck-saga detector, made honest. A saga that stops moving raises no error:
// its timeout was lost, a participant swallowed a message, a bug returned no messages.
// The only symptom is a row that stops changing, so something has to go and look.
//
// Detection only, and read-only: it never retries or "fixes" a saga. That is why every
// Orders node can run it without a distributed lock. Each node reports the same numbers,
// and the alert rules take max() across nodes.
//
// It polls on a timer and caches; the gauges read the cache. A Prometheus scrape (or an
// OTLP export) never runs a query, so a slow database can't turn the metrics endpoint
// into the thing that is down. saga_monitor_last_run_timestamp says how fresh the cache
// is, and has its own alert: a silent monitor must not look like a healthy system.
public sealed class SagaMonitor : BackgroundService
{
    // The monitor's own vocabulary. The index filters (OrdersDbContext) and the SQL
    // below spell the same values as literals; see the comment on PollAsync.
    private static readonly SagaStatus[] ActiveStatuses = [SagaStatus.InProgress, SagaStatus.Compensating];
    private static readonly SagaStatus[] AttentionStatuses = [SagaStatus.CompensationFailed, SagaStatus.NeedsManualReview];

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly SagaMonitorOptions _options;
    private readonly ILogger<SagaMonitor> _logger;
    private volatile SagaHealth? _last;

    public SagaMonitor(IServiceScopeFactory scopes, TimeProvider clock, SagaMonitorOptions options, IMeterFactory meters, ILogger<SagaMonitor> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _options = options;
        _logger = logger;

        var meter = meters.Create(SagaMetrics.MeterName);

        // Named with the unit already in them; no OTel unit, so no exporter appends a
        // second "_seconds".
        meter.CreateObservableGauge("saga_oldest_active_age_seconds",
            () => _last is { } h ? [new Measurement<double>(h.OldestActiveAgeSeconds)] : Array.Empty<Measurement<double>>(),
            description: "Seconds since the least recently updated InProgress or Compensating saga last moved; 0 when none are active");
        meter.CreateObservableGauge("saga_active",
            () => ByStatus(_last?.Active),
            description: "Sagas in flight, by status");
        meter.CreateObservableGauge("saga_stuck",
            () => _last is { } h ? [new Measurement<int>(h.StuckCount)] : Array.Empty<Measurement<int>>(),
            description: "In-flight sagas with no progress for longer than StuckAfter");
        meter.CreateObservableGauge("saga_needs_attention",
            () => ByStatus(_last?.NeedsAttention),
            description: "Sagas parked in CompensationFailed or NeedsManualReview until a human resolves them");
        meter.CreateObservableGauge("saga_monitor_last_run_timestamp",
            () => _last is { } h
                ? [new Measurement<double>(h.TakenUtc.ToUnixTimeMilliseconds() / 1000.0, new KeyValuePair<string, object?>("monitor", "sagas"))]
                : Array.Empty<Measurement<double>>(),
            description: "Unix time of the last successful poll");
    }

    public SagaHealth? Last => _last;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _clock);
        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // Keep the old numbers; the heartbeat going stale is the signal.
                _logger.LogError(e, "Saga monitor poll failed; keeping the previous values");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // The stuck-saga query. Two things make it cheap enough to run every minute on
    // every node, on a table that grows forever:
    //   - IX_CheckoutSagas_Active is a FILTERED index: it holds only the in-flight rows,
    //     so its size tracks what is in flight today, not every order ever placed.
    //   - The filter is static (Status IN (...)), and the rolling cutoff lives in the
    //     query. An index can't filter on "now minus 15 minutes".
    // The statuses are literals, not parameters, on purpose: SQL Server only matches a
    // filtered index when it can prove at compile time that the query's predicate
    // implies the filter, and it can't prove that about a parameter.
    internal async Task<SagaHealth> PollAsync(CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var now = _clock.GetUtcNow();
        var cutoff = now - _options.StuckAfter;

        var active = await db.Database.SqlQuery<StatusSummary>($"""
            SELECT Status, COUNT(*) AS Count, MIN(LastUpdatedUtc) AS OldestUpdatedUtc,
                   SUM(CASE WHEN LastUpdatedUtc < {cutoff} THEN 1 ELSE 0 END) AS Stuck
            FROM orders.CheckoutSagas
            WHERE Status IN ('InProgress', 'Compensating')
            GROUP BY Status
            """).ToListAsync(ct);

        var stuck = await db.Database.SqlQuery<StuckSaga>($"""
            SELECT TOP (20) Id, Status, CurrentStep, AttemptCount, LastUpdatedUtc
            FROM orders.CheckoutSagas
            WHERE Status IN ('InProgress', 'Compensating') AND LastUpdatedUtc < {cutoff}
            ORDER BY LastUpdatedUtc
            """).ToListAsync(ct);

        // Parked sagas stay in these states until someone resolves them (runbook), so
        // the alert keeps firing until that happens, not just once.
        var attention = await db.Database.SqlQueryRaw<StatusSummary>("""
            SELECT Status, COUNT(*) AS Count, NULL AS OldestUpdatedUtc, 0 AS Stuck
            FROM orders.CheckoutSagas
            WHERE Status IN ('CompensationFailed', 'NeedsManualReview')
            GROUP BY Status
            """).ToListAsync(ct);

        var oldest = active.Count == 0 ? (DateTimeOffset?)null : active.Min(a => a.OldestUpdatedUtc);
        var health = new SagaHealth(
            now,
            Tally(ActiveStatuses, active),
            Tally(AttentionStatuses, attention),
            // Reset to 0 when nothing is active; a gauge that keeps its last value would
            // report a stuck saga long after it finished.
            oldest is { } o ? Math.Max(0, (now - o).TotalSeconds) : 0,
            active.Sum(a => a.Stuck),
            stuck);

        foreach (var s in stuck)
        {
            _logger.LogWarning("Saga {SagaId} stuck: {Status} at {Step} attempt {Attempt}, no progress since {LastUpdatedUtc:O}",
                s.Id, s.Status, s.CurrentStep, s.AttemptCount, s.LastUpdatedUtc);
        }

        _last = health;
        return health;
    }

    private static Dictionary<SagaStatus, int> Tally(SagaStatus[] statuses, List<StatusSummary> rows) =>
        statuses.ToDictionary(s => s, s => rows.FirstOrDefault(r => r.Status == s.ToString())?.Count ?? 0);

    // Every status is reported, zeros included, so a series never vanishes and
    // max_over_time() and rate() see the drop back to 0.
    private static IEnumerable<Measurement<int>> ByStatus(IReadOnlyDictionary<SagaStatus, int>? counts) =>
        counts is null
            ? []
            : counts.Select(kv => new Measurement<int>(kv.Value, new KeyValuePair<string, object?>("status", kv.Key.ToString())));

    private sealed record StatusSummary(string Status, int Count, DateTimeOffset? OldestUpdatedUtc, int Stuck);
}
