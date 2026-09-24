using Contracts;
using Microsoft.EntityFrameworkCore;
using Orders.Data;
using Wolverine;

namespace Orders.Saga;

public enum NudgeResult
{
    Sent,
    NotFound,
    NotInFlight
}

public enum CancelResult
{
    Sent,
    NotFound,
    NotParked
}

// The runbook's lever for a stuck saga (docs/runbook.md#sagastuck). A saga stops moving
// when the message that would move it is gone: its scheduled timeout deleted, its command
// purged from a queue, a reply dead-lettered and discarded. The fix is to send it the
// very message it was waiting for: a StepTimeout for its current (step, attempt,
// direction). The saga then does what it would have done had the timeout fired: re-sends
// the command under the same CommandId, or moves on to the inquiry.
//
// Safe to run twice, or while the real timeout is still in flight: StepTimeout is gated on
// all three values. Whichever arrives second, or after the saga has moved, is discarded
// as stale. Nothing here writes to the saga row; only the saga itself does that.
public static class SagaOperations
{
    public static async Task<NudgeResult> Nudge(Guid sagaId, OrdersDbContext db, IMessageBus bus, CancellationToken ct = default)
    {
        var saga = await db.Sagas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sagaId, ct);
        if (saga is null) return NudgeResult.NotFound;
        if (saga.Status is not (SagaStatus.InProgress or SagaStatus.Compensating)) return NudgeResult.NotInFlight;

        var direction = saga.Status == SagaStatus.Compensating ? Direction.Compensate : Direction.Forward;
        await bus.SendAsync(new StepTimeout(saga.Id, saga.CurrentStep, saga.AttemptCount, direction, TimeSpan.Zero));
        return NudgeResult.Sent;
    }

    // The runbook's lever for a parked saga (docs/runbook.md#saganeedsattention), used
    // after a person has checked the provider and decided to cancel. The saga does the
    // undo itself; see CheckoutSaga.Handle(CancelParkedSaga).
    public static async Task<CancelResult> Cancel(Guid sagaId, string resolvedBy, string note, OrdersDbContext db, IMessageBus bus, CancellationToken ct = default)
    {
        var saga = await db.Sagas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sagaId, ct);
        if (saga is null) return CancelResult.NotFound;
        if (saga.Status is not (SagaStatus.NeedsManualReview or SagaStatus.CompensationFailed)) return CancelResult.NotParked;

        await bus.SendAsync(new CancelParkedSaga(saga.Id, resolvedBy, note));
        return CancelResult.Sent;
    }
}
