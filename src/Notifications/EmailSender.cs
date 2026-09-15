using System.Diagnostics.Metrics;

namespace Notifications;

public sealed class EmailOptions
{
    // Share of sends that fail, to exercise the retry policy and the failure alert.
    public double FailureRate { get; set; }
}

public sealed class EmailProviderException(string message) : Exception(message);

public sealed class NotificationMetrics
{
    public const string MeterName = "Saga.Notifications";
    private readonly Counter<long> _failures;
    private readonly Counter<long> _sent;

    public NotificationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _failures = meter.CreateCounter<long>("notification_failures");
        _sent = meter.CreateCounter<long>("notifications_sent");
    }

    public void Failed(string kind) => _failures.Add(1, new KeyValuePair<string, object?>("kind", kind));
    public void Sent(string kind) => _sent.Add(1, new KeyValuePair<string, object?>("kind", kind));
}

// Stand-in for an email provider. Logs instead of sending.
public sealed class EmailSender(EmailOptions options, ILogger<EmailSender> logger)
{
    public async Task SendAsync(string to, string subject, CancellationToken ct)
    {
        await Task.Delay(50, ct);
        if (Random.Shared.NextDouble() < options.FailureRate)
        {
            throw new EmailProviderException("email provider returned 503");
        }
        logger.LogInformation("Email to {To}: {Subject}", to, subject);
    }
}
