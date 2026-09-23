using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ServiceDefaults;

public sealed record DeadLetterHealth(DateTimeOffset TakenUtc, int Count, double OldestAgeSeconds);

// Watches this service's own dead-letter table (wolverine.wolverine_dead_letters, the one
// sink chosen in SagaMessaging). Same shape as the saga monitor: poll, cache, gauges read
// the cache.
//
// Why age and not depth: a dead letter sits there until a human replays or deletes it,
// so "depth > 0" stays red forever and trains everyone to ignore it. "The oldest one has
// been waiting over 10 minutes" means nobody has looked yet; the new-dead-letter rate
// (Wolverine's own wolverine-dead-letter-queue counter) says whether it is happening now.
public sealed class DeadLetterMonitor : BackgroundService
{
    public const string MeterName = "Saga.DeadLetters";
    private const int InvalidObjectName = 208;

    private readonly string _connectionString;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeadLetterMonitor> _logger;
    private volatile DeadLetterHealth? _last;

    public DeadLetterMonitor(string connectionString, TimeSpan interval, TimeProvider clock, IMeterFactory meters, ILogger<DeadLetterMonitor> logger)
    {
        _connectionString = connectionString;
        _interval = interval;
        _clock = clock;
        _logger = logger;

        var meter = meters.Create(MeterName);
        meter.CreateObservableGauge("dead_letters",
            () => _last is { } h ? [new Measurement<int>(h.Count)] : Array.Empty<Measurement<int>>(),
            description: "Messages parked in this service's dead-letter table");
        meter.CreateObservableGauge("dead_letter_oldest_age_seconds",
            () => _last is { } h ? [new Measurement<double>(h.OldestAgeSeconds)] : Array.Empty<Measurement<double>>(),
            description: "Seconds since the oldest dead letter was first sent; 0 when there are none");
        meter.CreateObservableGauge("saga_monitor_last_run_timestamp",
            () => _last is { } h
                ? [new Measurement<double>(h.TakenUtc.ToUnixTimeMilliseconds() / 1000.0, new KeyValuePair<string, object?>("monitor", "dead_letters"))]
                : Array.Empty<Measurement<double>>(),
            description: "Unix time of the last successful poll");
    }

    public DeadLetterHealth? Last => _last;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _clock);
        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (SqlException e) when (e.Number == InvalidObjectName && _last is null)
            {
                // Wolverine creates its tables while the host starts, alongside this.
                _logger.LogInformation("Dead-letter table not there yet; next poll in {Interval}", _interval);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(e, "Dead-letter monitor poll failed; keeping the previous values");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Wolverine's table records when a message was first sent, not when it was parked.
    // For most dead letters those are seconds apart. For one that spent its scheduled
    // retries first (up to ~13 minutes for DependencyUnavailableException) the age runs
    // ahead of the parking time. For a message nobody has processed, measuring from when
    // it was sent is the honest number anyway.
    // A message that arrived without Wolverine's headers (published by hand from the
    // RabbitMQ UI, or by a foreign producer) is stored with sent_at = 0001-01-01. It
    // counts, but it has no age; the alert is on the count for that reason.
    public async Task<DeadLetterHealth> PollAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(*), MIN(CASE WHEN sent_at > '2000-01-01' THEN sent_at END) FROM wolverine.wolverine_dead_letters", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var now = _clock.GetUtcNow();
        var count = reader.GetInt32(0);
        var oldest = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(1);
        var health = new DeadLetterHealth(now, count, oldest is { } o ? Math.Max(0, (now - o).TotalSeconds) : 0);

        _last = health;
        return health;
    }
}

public static class DeadLetterMonitorRegistration
{
    public static IServiceCollection AddDeadLetterMonitor(this IServiceCollection services, string connectionString, TimeSpan? interval = null)
    {
        services.AddSingleton(sp => new DeadLetterMonitor(
            connectionString,
            interval ?? TimeSpan.FromSeconds(60),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            sp.GetRequiredService<ILogger<DeadLetterMonitor>>()));
        services.AddHostedService(sp => sp.GetRequiredService<DeadLetterMonitor>());
        return services;
    }
}
