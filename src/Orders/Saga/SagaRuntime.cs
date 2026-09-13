using Microsoft.Extensions.Logging;

namespace Orders.Saga;

// Everything a saga handler needs besides its own state, injected by Wolverine per call.
// A plain object so unit tests can build one with a FakeTimeProvider.
public sealed record SagaRuntime(TimeProvider Clock, SagaTimings Timings, SagaMetrics Metrics, ILogger<CheckoutSaga> Logger);
