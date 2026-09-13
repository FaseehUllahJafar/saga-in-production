namespace Contracts;

// Every command and reply carries SagaId (Wolverine correlates on that name) and the
// CommandId it belongs to. Replies are matched on (SagaId, step, CommandId), never on
// an attempt number: a participant answering a retry from its dedupe table sends the
// same reply it sent the first time, and that answer is still valid.

public sealed record OrderLine(Guid OrderLineId, string Sku, int Quantity, decimal UnitPrice);

// ---- Payments ----------------------------------------------------------------------
public sealed record AuthorizePayment(Guid SagaId, Guid CommandId, decimal Amount, string CardToken);
public sealed record PaymentAuthorized(Guid SagaId, Guid CommandId, string AuthorizationId);
public sealed record PaymentDeclined(Guid SagaId, Guid CommandId, string Reason);

public sealed record VoidPayment(Guid SagaId, Guid CommandId);
public sealed record PaymentVoided(Guid SagaId, Guid CommandId, bool WasNoOp);

public sealed record CapturePayment(Guid SagaId, Guid CommandId, decimal Amount);
public sealed record PaymentCaptured(Guid SagaId, Guid CommandId, string CaptureId);
public sealed record CaptureFailed(Guid SagaId, Guid CommandId, bool IsTerminal, string Reason);

// ---- Inventory ---------------------------------------------------------------------
public sealed record ReserveStock(Guid SagaId, Guid CommandId, IReadOnlyList<OrderLine> Lines);
public sealed record StockReserved(Guid SagaId, Guid CommandId);
public sealed record InsufficientStock(Guid SagaId, Guid CommandId, string Sku);

public sealed record ReleaseStock(Guid SagaId, Guid CommandId);
public sealed record StockReleased(Guid SagaId, Guid CommandId, bool WasNoOp);

// ---- Shipping ----------------------------------------------------------------------
public sealed record BookShipment(Guid SagaId, Guid CommandId, string Address, int ItemCount);
public sealed record ShipmentBooked(Guid SagaId, Guid CommandId, string TrackingNumber);
public sealed record ShipmentRejected(Guid SagaId, Guid CommandId, string Reason);

public sealed record CancelShipment(Guid SagaId, Guid CommandId);
public sealed record ShipmentCancelled(Guid SagaId, Guid CommandId, bool WasNoOp);

// ---- Inquiry: "did my command land?" -----------------------------------------------
// Sent by the orchestrator when a step's retries are exhausted. The participant looks
// the CommandId up (Payments asks FakePay by idempotency key) and reports what it finds.
public sealed record CheckStepStatus(Guid SagaId, Guid CommandId, string Step);
public sealed record StepStatusReported(Guid SagaId, Guid CommandId, string Step, InquiryResult Result, string? Reference);

public enum InquiryResult
{
    Succeeded,
    Failed,
    NotFound
}

// ---- Integration events (outside the saga) -----------------------------------------
public sealed record OrderCompleted(Guid OrderId, string CustomerEmail, decimal Amount);
public sealed record OrderCancelled(Guid OrderId, string CustomerEmail, string Reason);
