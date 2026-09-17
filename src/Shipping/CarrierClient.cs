using System.Collections.Concurrent;

namespace Shipping;

public sealed class CarrierOptions
{
    public int LatencyMs { get; set; } = 150;
    // Share of calls answered with a 5xx.
    public double FailureRate { get; set; }
    // Share of calls that hang past the caller's patience.
    public double TimeoutRate { get; set; }
}

public abstract record CarrierResult
{
    public sealed record Booked(string TrackingNumber) : CarrierResult;
    public sealed record Rejected(string Reason) : CarrierResult;
    public sealed record Unavailable(string Reason) : CarrierResult;
}

// A flaky third-party carrier, simulated in-process: latency, 5xx and hangs. It keys
// bookings by the caller's idempotency key like a real carrier API would. The ledger is
// in memory, so a Shipping restart forgets it; that is a limit of the stub, not of the
// pattern (Payments has a real persisted provider in FakePay).
public sealed class CarrierClient(CarrierOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, string> _bookings = new();
    private readonly ConcurrentDictionary<string, bool> _cancelled = new();
    private readonly ConcurrentDictionary<Guid, int> _bookAttempts = new();

    public int BookAttempts(Guid idempotencyKey) => _bookAttempts.GetValueOrDefault(idempotencyKey);

    public bool IsCancelled(string trackingNumber) => _cancelled.ContainsKey(trackingNumber);

    public async Task<CarrierResult> BookAsync(Guid idempotencyKey, string address, CancellationToken ct)
    {
        _bookAttempts.AddOrUpdate(idempotencyKey, 1, (_, n) => n + 1);
        if (await Flake(ct) is { } failure) return failure;

        if (address.Contains("UNDELIVERABLE", StringComparison.OrdinalIgnoreCase))
        {
            return new CarrierResult.Rejected("address not serviceable");
        }

        var tracking = _bookings.GetOrAdd(idempotencyKey, _ => $"TRK{clock.GetUtcNow():yyMMdd}{Random.Shared.Next(100000, 999999)}");
        return new CarrierResult.Booked(tracking);
    }

    public async Task<CarrierResult> CancelAsync(string trackingNumber, CancellationToken ct)
    {
        if (await Flake(ct) is { } failure) return failure;
        _cancelled[trackingNumber] = true;
        return new CarrierResult.Booked(trackingNumber);
    }

    public string? FindByKey(Guid idempotencyKey) => _bookings.GetValueOrDefault(idempotencyKey);

    private async Task<CarrierResult?> Flake(CancellationToken ct)
    {
        await Task.Delay(options.LatencyMs, ct);
        var roll = Random.Shared.NextDouble();
        if (roll < options.TimeoutRate) return new CarrierResult.Unavailable("carrier timed out");
        if (roll < options.TimeoutRate + options.FailureRate) return new CarrierResult.Unavailable("carrier returned 503");
        return null;
    }
}
