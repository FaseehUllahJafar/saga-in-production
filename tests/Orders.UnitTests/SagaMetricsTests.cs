using System.Diagnostics.Metrics;
using Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orders.Saga;

namespace Orders.UnitTests;

public class SagaMetricsTests
{
    // Production failure: a counter series first exported at 22 gives increase() no earlier
    // sample, so 22 compensations inside one export interval read as almost none, and the
    // compensation-rate alert stayed quiet on the first live run.
    [Fact]
    public void EveryCounterSeries_StartsAtZero_BeforeAnythingHappens()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var recorded = new List<(string Name, long Value, string? Step)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory)) l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            string? step = null;
            foreach (var tag in tags) if (tag.Key == "step") step = tag.Value as string;
            recorded.Add((i.Name, v, step));
        });
        listener.Start();

        _ = new SagaMetrics(meterFactory);

        recorded.ShouldAllBe(r => r.Value == 0);
        recorded.ShouldContain(r => r.Name == "saga_started");
        recorded.ShouldContain(r => r.Name == "saga_completed");
        foreach (var name in new[] { "saga_compensations", "saga_compensation_failed", "saga_manual_review" })
        {
            recorded.Where(r => r.Name == name).Select(r => r.Step).ShouldBe(StepNames.All, ignoreOrder: true);
        }
    }
}
