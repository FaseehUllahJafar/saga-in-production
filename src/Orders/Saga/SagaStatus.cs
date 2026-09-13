namespace Orders.Saga;

// Stored as strings: the stuck-saga query and its filtered index name these values,
// and a string survives someone reordering the enum.
public enum SagaStatus
{
    InProgress,
    Compensating,
    Completed,
    Cancelled,
    CompensationFailed,
    // Past the pivot with the capture outcome unknown. Neither direction is safe.
    NeedsManualReview
}
