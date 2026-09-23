using Contracts;
using System.Diagnostics.Metrics;

namespace Orders.Saga;

public sealed class SagaMetrics
{
    public const string MeterName = "Saga.Orders";

    private readonly Counter<long> _started;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _compensations;
    private readonly Counter<long> _compensationFailed;
    private readonly Counter<long> _staleDiscarded;
    private readonly Counter<long> _manualReview;

    public SagaMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _started = meter.CreateCounter<long>("saga_started");
        _completed = meter.CreateCounter<long>("saga_completed");
        _compensations = meter.CreateCounter<long>("saga_compensations");
        _compensationFailed = meter.CreateCounter<long>("saga_compensation_failed");
        _staleDiscarded = meter.CreateCounter<long>("saga_stale_messages_discarded");
        _manualReview = meter.CreateCounter<long>("saga_manual_review");

        // Every series starts life at 0. A counter series that is first exported at 22
        // (22 declines inside one export interval) gives increase() and rate() no earlier
        // sample to measure from, and the alert rules would see nothing happen. Found on
        // the first live run: 22 of 28 sagas compensated read as 18%.
        _started.Add(0);
        _completed.Add(0);
        foreach (var step in StepNames.All)
        {
            var tag = new KeyValuePair<string, object?>("step", step);
            _compensations.Add(0, tag);
            _compensationFailed.Add(0, tag);
            _manualReview.Add(0, tag);
        }
    }

    public void Started() => _started.Add(1);
    public void Completed() => _completed.Add(1);
    public void CompensationStarted(string failedStep) => _compensations.Add(1, new KeyValuePair<string, object?>("step", failedStep));
    public void CompensationFailed(string step) => _compensationFailed.Add(1, new KeyValuePair<string, object?>("step", step));
    public void ManualReview(string step) => _manualReview.Add(1, new KeyValuePair<string, object?>("step", step));
    public void StaleDiscarded(string messageType) => _staleDiscarded.Add(1, new KeyValuePair<string, object?>("message", messageType));
}
