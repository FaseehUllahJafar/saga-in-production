namespace Orders.Saga;

public sealed class SagaTimings
{
    // Forward step: how long to wait for a reply before the next attempt. The number of
    // entries is the number of attempts (ADR 0003: business retries belong to the saga).
    public TimeSpan[] ForwardAttemptTimeouts { get; set; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)];

    // After the last forward attempt: how long to wait for the "did it land?" inquiry.
    public TimeSpan InquiryTimeout { get; set; } = TimeSpan.FromMinutes(1);

    // Compensations retry longer: giving up on one leaves a real effect in the world.
    public TimeSpan[] CompensationAttemptTimeouts { get; set; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8)];
}
