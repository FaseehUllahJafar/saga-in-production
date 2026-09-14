using Contracts;
using Microsoft.EntityFrameworkCore;
using Payments.Data;
using ServiceDefaults;
using Wolverine;

namespace Payments.Handlers;

public static class AuthorizePaymentHandler
{
    public static async Task<OutgoingMessages> Handle(
        AuthorizePayment cmd, PaymentsDbContext db, FakePayClient fakePay, TimeProvider clock, ILogger<AuthorizePayment> logger, CancellationToken ct)
    {
        var step = await PaymentSteps.GetOrCreatePending(db, cmd.SagaId, StepNames.AuthorizePayment, cmd.CommandId, clock, ct);

        switch (step.Status)
        {
            // Duplicate or retry of a command that already has an answer: send the SAME
            // answer again. An idempotent success, never an exception, never a DLQ entry.
            case PaymentStepStatus.Authorized:
                return [new PaymentAuthorized(cmd.SagaId, cmd.CommandId, step.ProviderRef!)];
            case PaymentStepStatus.Declined:
                return [new PaymentDeclined(cmd.SagaId, cmd.CommandId, step.Reason!)];
            case PaymentStepStatus.Cancelled or PaymentStepStatus.Voiding or PaymentStepStatus.Voided:
                // The tombstone. The void got here first, so this late authorize must not
                // create an authorization nobody will ever release. No reply: the saga
                // has moved on and would discard it anyway.
                logger.LogWarning("Saga {SagaId}: authorize arrived after its void; rejected by tombstone", cmd.SagaId);
                return [];
        }

        // Pending: first attempt, or a retry after we lost track of an earlier one. The
        // CommandId is the idempotency key, so FakePay replays rather than double-charging.
        var result = await fakePay.AuthorizeAsync(cmd.CommandId, cmd.SagaId, cmd.Amount, cmd.CardToken, ct);
        switch (result)
        {
            case ProviderResult.Ok ok:
                step.Status = PaymentStepStatus.Authorized;
                step.ProviderRef = ok.Authorization.Id;
                break;
            case ProviderResult.Rejected rejected:
                step.Status = PaymentStepStatus.Declined;
                step.Reason = rejected.Error;
                break;
            default:
                // No answer. Leave the row Pending and say nothing: the saga's timeout
                // decides whether to retry, and a retry will find this row.
                return [];
        }

        step.UpdatedUtc = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else wrote this row while we were at FakePay. Look at what they wrote
            // before acting: it may be a duplicate of this very command that got there
            // first (same idempotency key, so the SAME authorization), not a void.
            db.ChangeTracker.Clear();
            var winner = (await db.Steps.FindAsync([cmd.SagaId, StepNames.AuthorizePayment], ct))!;
            if (winner.Status is PaymentStepStatus.Cancelled or PaymentStepStatus.Voiding or PaymentStepStatus.Voided
                && result is ProviderResult.Ok landed)
            {
                // A void tombstoned the row mid-call. The provider may now hold an
                // authorization the saga has already written off: release it here.
                logger.LogWarning("Saga {SagaId}: authorization {AuthorizationId} landed after cancel; voiding it", cmd.SagaId, landed.Authorization.Id);
                await fakePay.VoidAsync(landed.Authorization.Id, cmd.SagaId, ct);
            }
            return [];
        }

        return step.Status == PaymentStepStatus.Authorized
            ? [new PaymentAuthorized(cmd.SagaId, cmd.CommandId, step.ProviderRef!)]
            : [new PaymentDeclined(cmd.SagaId, cmd.CommandId, step.Reason!)];
    }
}

public static class VoidPaymentHandler
{
    public static async Task<OutgoingMessages> Handle(
        VoidPayment cmd, PaymentsDbContext db, FakePayClient fakePay, TimeProvider clock, ILogger<VoidPayment> logger, CancellationToken ct)
    {
        var step = await db.Steps.FindAsync([cmd.SagaId, StepNames.AuthorizePayment], ct);

        if (step is null)
        {
            // Void before authorize: write the tombstone and report a no-op success.
            // Compensating something that never happened is not an error.
            if (await PaymentSteps.TryInsertTombstone(db, cmd.SagaId, StepNames.AuthorizePayment, clock, ct))
            {
                return [new PaymentVoided(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
            }
            step = (await db.Steps.FindAsync([cmd.SagaId, StepNames.AuthorizePayment], ct))!;
        }

        switch (step.Status)
        {
            case PaymentStepStatus.Voided:
                return [new PaymentVoided(cmd.SagaId, cmd.CommandId, WasNoOp: false)];
            case PaymentStepStatus.Declined or PaymentStepStatus.Cancelled:
                return [new PaymentVoided(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
            case PaymentStepStatus.Pending or PaymentStepStatus.Voiding:
                return await VoidUnknown(cmd, step, db, fakePay, clock, logger, ct);
            default:
                return await VoidAuthorized(cmd, step, step.ProviderRef!, db, fakePay, clock, logger, ct);
        }
    }

    // We don't know if the authorize landed. Tombstone first (so an authorize still in
    // flight voids itself on the way out), then ask FakePay by idempotency key and act on
    // the answer. If FakePay doesn't answer, the row stays Voiding and we say nothing:
    // the saga's compensation timeout re-sends the void and we ask again.
    private static async Task<OutgoingMessages> VoidUnknown(
        VoidPayment cmd, PaymentStep step, PaymentsDbContext db, FakePayClient fakePay, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        if (step.Status == PaymentStepStatus.Pending)
        {
            step.Status = PaymentStepStatus.Voiding;
            step.UpdatedUtc = clock.GetUtcNow();
            // If the in-flight authorize commits first, this throws a concurrency conflict
            // and the retry policy re-runs the void against the fresh (Authorized) row.
            await db.SaveChangesAsync(ct);
        }

        var lookup = await fakePay.LookupAsync("authorize", step.CommandId, cmd.SagaId, ct);
        switch (lookup)
        {
            case ProviderResult.Ok { Authorization.Status: "authorized" } found:
                logger.LogWarning("Saga {SagaId}: authorize timed out but the charge landed ({AuthorizationId}); voiding it", cmd.SagaId, found.Authorization.Id);
                return await VoidAuthorized(cmd, step, found.Authorization.Id, db, fakePay, clock, logger, ct);
            case ProviderResult.Ok or ProviderResult.NotFound or ProviderResult.Rejected:
                step.Status = PaymentStepStatus.Cancelled;
                step.UpdatedUtc = clock.GetUtcNow();
                return [new PaymentVoided(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
            default:
                return [];
        }
    }

    private static async Task<OutgoingMessages> VoidAuthorized(
        VoidPayment cmd, PaymentStep step, string authorizationId, PaymentsDbContext db, FakePayClient fakePay, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var result = await fakePay.VoidAsync(authorizationId, cmd.SagaId, ct);
        switch (result)
        {
            case ProviderResult.Ok:
                step.Status = PaymentStepStatus.Voided;
                step.ProviderRef = authorizationId;
                step.UpdatedUtc = clock.GetUtcNow();
                return [new PaymentVoided(cmd.SagaId, cmd.CommandId, WasNoOp: false)];
            case ProviderResult.Rejected rejected:
                // e.g. already captured. Not something a retry fixes; the saga's
                // compensation timeouts will run out and raise CompensationFailed.
                logger.LogError("Saga {SagaId}: FakePay refused to void {AuthorizationId}: {Error}", cmd.SagaId, authorizationId, rejected.Error);
                return [];
            default:
                return [];
        }
    }
}

public static class CapturePaymentHandler
{
    public static async Task<OutgoingMessages> Handle(
        CapturePayment cmd, PaymentsDbContext db, FakePayClient fakePay, TimeProvider clock, CancellationToken ct)
    {
        var authorization = await db.Steps.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SagaId == cmd.SagaId && s.Step == StepNames.AuthorizePayment, ct);
        if (authorization is not { Status: PaymentStepStatus.Authorized, ProviderRef: { } authorizationId })
        {
            return [new CaptureFailed(cmd.SagaId, cmd.CommandId, IsTerminal: true, "no live authorization")];
        }

        var step = await PaymentSteps.GetOrCreatePending(db, cmd.SagaId, StepNames.CapturePayment, cmd.CommandId, clock, ct);
        switch (step.Status)
        {
            case PaymentStepStatus.Captured:
                return [new PaymentCaptured(cmd.SagaId, cmd.CommandId, step.ProviderRef!)];
            case PaymentStepStatus.CaptureFailed:
                return [new CaptureFailed(cmd.SagaId, cmd.CommandId, IsTerminal: true, step.Reason!)];
        }

        var result = await fakePay.CaptureAsync(authorizationId, cmd.CommandId, cmd.SagaId, cmd.Amount, ct);
        switch (result)
        {
            case ProviderResult.Ok ok:
                step.Status = PaymentStepStatus.Captured;
                step.ProviderRef = ok.Authorization.CaptureId;
                step.UpdatedUtc = clock.GetUtcNow();
                return [new PaymentCaptured(cmd.SagaId, cmd.CommandId, step.ProviderRef!)];
            case ProviderResult.Rejected or ProviderResult.NotFound:
                var reason = result is ProviderResult.Rejected r ? r.Error : "authorization not found";
                step.Status = PaymentStepStatus.CaptureFailed;
                step.Reason = reason;
                step.UpdatedUtc = clock.GetUtcNow();
                return [new CaptureFailed(cmd.SagaId, cmd.CommandId, IsTerminal: true, reason)];
            case ProviderResult.Unavailable unavailable:
                return [new CaptureFailed(cmd.SagaId, cmd.CommandId, IsTerminal: false, unavailable.Reason)];
            default:
                return [];
        }
    }
}

public static class PaymentInquiryHandler
{
    public static async Task<OutgoingMessages> Handle(CheckStepStatus query, PaymentsDbContext db, FakePayClient fakePay, TimeProvider clock, CancellationToken ct)
    {
        var operation = query.Step == StepNames.CapturePayment ? "capture" : "authorize";
        var local = await db.Steps.FindAsync([query.SagaId, query.Step], ct);

        // FakePay is the source of truth for "did it happen": our own row may still say
        // Pending precisely because the response was lost.
        var lookup = await fakePay.LookupAsync(operation, query.CommandId, query.SagaId, ct);
        StepStatusReported Report(InquiryResult result, string? reference = null) =>
            new(query.SagaId, query.CommandId, query.Step, result, reference);

        switch (lookup)
        {
            case ProviderResult.Ok ok:
                var reference = operation == "capture" ? ok.Authorization.CaptureId : ok.Authorization.Id;
                if (local is { Status: PaymentStepStatus.Pending })
                {
                    local.Status = operation == "capture" ? PaymentStepStatus.Captured : PaymentStepStatus.Authorized;
                    local.ProviderRef = reference;
                    local.UpdatedUtc = clock.GetUtcNow();
                }
                return [Report(InquiryResult.Succeeded, reference)];
            case ProviderResult.Rejected:
                return [Report(InquiryResult.Failed)];
            case ProviderResult.NotFound:
                return [Report(InquiryResult.NotFound)];
            default:
                return [];
        }
    }
}

internal static class PaymentSteps
{
    public static async Task<PaymentStep> GetOrCreatePending(PaymentsDbContext db, Guid sagaId, string step, Guid commandId, TimeProvider clock, CancellationToken ct)
    {
        if (await db.Steps.FindAsync([sagaId, step], ct) is { } existing)
        {
            return existing;
        }

        var pending = new PaymentStep { SagaId = sagaId, Step = step, CommandId = commandId, Status = PaymentStepStatus.Pending, UpdatedUtc = clock.GetUtcNow() };
        db.Steps.Add(pending);
        try
        {
            // Committed BEFORE the provider call, on purpose (Lightweight transaction
            // mode). If we die mid-call, the Pending row is the evidence that tells the
            // next attempt, the inquiry and the void to go and ask FakePay.
            await db.SaveChangesAsync(ct);
            return pending;
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            // Lost a race with a duplicate or with a tombstone. Use the winner's row.
            db.ChangeTracker.Clear();
            return (await db.Steps.FindAsync([sagaId, step], ct))!;
        }
    }

    public static async Task<bool> TryInsertTombstone(PaymentsDbContext db, Guid sagaId, string step, TimeProvider clock, CancellationToken ct)
    {
        // CommandId always names the FORWARD command the row is about, tombstone or not.
        var commandId = CommandId.For(sagaId, step, Direction.Forward);
        db.Steps.Add(new PaymentStep { SagaId = sagaId, Step = step, CommandId = commandId, Status = PaymentStepStatus.Cancelled, UpdatedUtc = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
