namespace Contracts;

// Step names feed CommandId hashing and are stored in the saga journal and in every
// participant's idempotency table. Renaming one changes every CommandId derived from
// it, so treat these strings as a persisted format: add new ones, never edit old ones.
public static class StepNames
{
    public const string AuthorizePayment = "authorize-payment";
    public const string ReserveStock = "reserve-stock";
    public const string BookShipment = "book-shipment";
    public const string CapturePayment = "capture-payment";
}

public enum Direction
{
    Forward,
    Compensate
}
