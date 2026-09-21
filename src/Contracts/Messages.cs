namespace Contracts;

// Every command and reply carries SagaId (Wolverine correlates on that name) and the
// CommandId it belongs to. Replies are matched on (SagaId, step, CommandId), never on
// an attempt number: a participant answering a retry from its dedupe table sends the
// same reply it sent the first time, and that answer is still valid.

// What every saga message has in common. Lets shared plumbing (the SagaId log scope
// and span tag, ServiceDefaults.SagaIdMiddleware) find the id without knowing the type.
public interface ISagaMessage
{
    Guid SagaId { get; }
}

public sealed record OrderLine(Guid OrderLineId, string Sku, int Quantity, decimal UnitPrice);

// ---- Payments ----------------------------------------------------------------------
public sealed record AuthorizePayment(Guid SagaId, Guid CommandId, decimal Amount, string CardToken) : ISagaMessage;
public sealed record PaymentAuthorized(Guid SagaId, Guid CommandId, string AuthorizationId) : ISagaMessage;
public sealed record PaymentDeclined(Guid SagaId, Guid CommandId, string Reason) : ISagaMessage;

public sealed record VoidPayment(Guid SagaId, Guid CommandId) : ISagaMessage;
public sealed record PaymentVoided(Guid SagaId, Guid CommandId, bool WasNoOp) : ISagaMessage;

public sealed record CapturePayment(Guid SagaId, Guid CommandId, decimal Amount) : ISagaMessage;
public sealed record PaymentCaptured(Guid SagaId, Guid CommandId, string CaptureId) : ISagaMessage;
public sealed record CaptureFailed(Guid SagaId, Guid CommandId, bool IsTerminal, string Reason) : ISagaMessage;

// ---- Inventory ---------------------------------------------------------------------
public sealed record ReserveStock(Guid SagaId, Guid CommandId, IReadOnlyList<OrderLine> Lines) : ISagaMessage;
public sealed record StockReserved(Guid SagaId, Guid CommandId) : ISagaMessage;
public sealed record InsufficientStock(Guid SagaId, Guid CommandId, string Sku) : ISagaMessage;

public sealed record ReleaseStock(Guid SagaId, Guid CommandId) : ISagaMessage;
public sealed record StockReleased(Guid SagaId, Guid CommandId, bool WasNoOp) : ISagaMessage;

// ---- Shipping ----------------------------------------------------------------------
public sealed record BookShipment(Guid SagaId, Guid CommandId, string Address, int ItemCount) : ISagaMessage;
public sealed record ShipmentBooked(Guid SagaId, Guid CommandId, string TrackingNumber) : ISagaMessage;
public sealed record ShipmentRejected(Guid SagaId, Guid CommandId, string Reason) : ISagaMessage;

public sealed record CancelShipment(Guid SagaId, Guid CommandId) : ISagaMessage;
public sealed record ShipmentCancelled(Guid SagaId, Guid CommandId, bool WasNoOp) : ISagaMessage;

// ---- Inquiry: "did my command land?" -----------------------------------------------
// Sent by the orchestrator when a step's retries are exhausted. The participant looks
// the CommandId up (Payments asks FakePay by idempotency key) and reports what it finds.
public sealed record CheckStepStatus(Guid SagaId, Guid CommandId, string Step) : ISagaMessage;
public sealed record StepStatusReported(Guid SagaId, Guid CommandId, string Step, InquiryResult Result, string? Reference) : ISagaMessage;

public enum InquiryResult
{
    Succeeded,
    Failed,
    NotFound
}

// ---- Integration events (outside the saga) -----------------------------------------
// The order id IS the saga id; the events keep the name their subscribers know it by.
public sealed record OrderCompleted(Guid OrderId, string CustomerEmail, decimal Amount) : ISagaMessage
{
    Guid ISagaMessage.SagaId => OrderId;
}
public sealed record OrderCancelled(Guid OrderId, string CustomerEmail, string Reason) : ISagaMessage
{
    Guid ISagaMessage.SagaId => OrderId;
}
