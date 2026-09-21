using Contracts;
using Wolverine;

namespace Orders.Saga;

public sealed record StartCheckout(
    Guid SagaId,
    string CustomerEmail,
    string CardToken,
    string ShippingAddress,
    IReadOnlyList<OrderLine> Lines) : ISagaMessage;

// The orchestrator's own timer. Unlike replies, a timeout carries (Step, Attempt,
// Direction), and it is discarded unless all three still match the saga row. A timeout
// scheduled for attempt 1 of the forward authorize step must not fire into attempt 2,
// and must never fire while the saga is compensating that same step.
public sealed record StepTimeout(Guid SagaId, string Step, int Attempt, Direction Direction, TimeSpan Delay)
    : TimeoutMessage(Delay), ISagaMessage;
