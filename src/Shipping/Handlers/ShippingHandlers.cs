using Contracts;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;
using Shipping.Data;
using Wolverine;

namespace Shipping.Handlers;

// Shipping's problem is a flaky dependency. Same shape as Payments: write Pending, call
// out with no transaction open, then record the answer. When the carrier doesn't answer
// we stay silent and let the saga's timeout decide.
public static class BookShipmentHandler
{
    public static async Task<OutgoingMessages> Handle(
        BookShipment cmd, ShippingDbContext db, CarrierClient carrier, TimeProvider clock, ILogger<BookShipment> logger, CancellationToken ct)
    {
        var shipment = await db.Shipments.FindAsync([cmd.SagaId], ct);
        if (shipment is null)
        {
            shipment = new Shipment { SagaId = cmd.SagaId, CommandId = cmd.CommandId, Status = ShipmentStatus.Pending, UpdatedUtc = clock.GetUtcNow() };
            db.Shipments.Add(shipment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (e.IsUniqueViolation())
            {
                db.ChangeTracker.Clear();
                shipment = (await db.Shipments.FindAsync([cmd.SagaId], ct))!;
            }
        }

        switch (shipment.Status)
        {
            case ShipmentStatus.Booked:
                return [new ShipmentBooked(cmd.SagaId, cmd.CommandId, shipment.TrackingNumber!)];
            case ShipmentStatus.Rejected:
                return [new ShipmentRejected(cmd.SagaId, cmd.CommandId, shipment.Reason!)];
            case ShipmentStatus.Cancelled:
                logger.LogWarning("Saga {SagaId}: booking arrived after its cancel; rejected by tombstone", cmd.SagaId);
                await CancelAnythingThatLanded(cmd, carrier, logger, ct);
                return [];
        }

        var result = await carrier.BookAsync(cmd.CommandId, cmd.Address, ct);
        switch (result)
        {
            case CarrierResult.Booked booked:
                shipment.Status = ShipmentStatus.Booked;
                shipment.TrackingNumber = booked.TrackingNumber;
                break;
            case CarrierResult.Rejected rejected:
                shipment.Status = ShipmentStatus.Rejected;
                shipment.Reason = rejected.Reason;
                break;
            default:
                return [];
        }

        shipment.UpdatedUtc = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else wrote the row mid-call. Only a cancel means our booking must be
            // undone; a duplicate of this command that won the race booked the SAME
            // shipment (same idempotency key) and must be left alone.
            db.ChangeTracker.Clear();
            var winner = (await db.Shipments.FindAsync([cmd.SagaId], ct))!;
            if (winner.Status == ShipmentStatus.Cancelled)
            {
                await CancelAnythingThatLanded(cmd, carrier, logger, ct);
            }
            return [];
        }

        return shipment.Status == ShipmentStatus.Booked
            ? [new ShipmentBooked(cmd.SagaId, cmd.CommandId, shipment.TrackingNumber!)]
            : [new ShipmentRejected(cmd.SagaId, cmd.CommandId, shipment.Reason!)];
    }

    // Throws when the carrier can't be reached so the transport retries: once the saga
    // has cancelled this step, nobody else will ever come back for a late booking.
    private static async Task CancelAnythingThatLanded(BookShipment cmd, CarrierClient carrier, ILogger logger, CancellationToken ct)
    {
        if (carrier.FindByKey(cmd.CommandId) is not { } tracking || carrier.IsCancelled(tracking)) return;

        logger.LogWarning("Saga {SagaId}: booking {TrackingNumber} landed after cancel; cancelling it", cmd.SagaId, tracking);
        if (await carrier.CancelAsync(tracking, ct) is CarrierResult.Unavailable failed)
        {
            throw new DependencyUnavailableException($"carrier cancel of {tracking} failed: {failed.Reason}");
        }
    }
}

public static class CancelShipmentHandler
{
    public static async Task<OutgoingMessages> Handle(CancelShipment cmd, ShippingDbContext db, CarrierClient carrier, TimeProvider clock, CancellationToken ct)
    {
        var shipment = await db.Shipments.FindAsync([cmd.SagaId], ct);
        if (shipment is null)
        {
            // Cancel before book: tombstone, no-op success. A duplicate cancel racing this
            // insert hits the primary key and is re-run by the retry policy.
            db.Shipments.Add(new Shipment
            {
                SagaId = cmd.SagaId,
                CommandId = CommandId.For(cmd.SagaId, StepNames.BookShipment, Direction.Forward),
                Status = ShipmentStatus.Cancelled,
                UpdatedUtc = clock.GetUtcNow()
            });
            return [new ShipmentCancelled(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
        }

        switch (shipment.Status)
        {
            case ShipmentStatus.Cancelled when shipment.TrackingNumber is not null:
                return [new ShipmentCancelled(cmd.SagaId, cmd.CommandId, WasNoOp: false)];
            case ShipmentStatus.Cancelled or ShipmentStatus.Rejected:
                return [new ShipmentCancelled(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
        }

        // Pending: the booking may be in flight. Ask the carrier by idempotency key; if it
        // hasn't landed, the tombstone makes the in-flight booking cancel itself.
        var tracking = shipment.TrackingNumber ?? carrier.FindByKey(shipment.CommandId);
        if (tracking is not null && await carrier.CancelAsync(tracking, ct) is CarrierResult.Unavailable)
        {
            return [];
        }

        shipment.Status = ShipmentStatus.Cancelled;
        shipment.TrackingNumber = tracking;
        shipment.UpdatedUtc = clock.GetUtcNow();
        return [new ShipmentCancelled(cmd.SagaId, cmd.CommandId, WasNoOp: tracking is null)];
    }
}

public static class ShippingInquiryHandler
{
    public static async Task<OutgoingMessages> Handle(CheckStepStatus query, ShippingDbContext db, CarrierClient carrier, CancellationToken ct)
    {
        var shipment = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.SagaId == query.SagaId, ct);
        var tracking = shipment?.TrackingNumber ?? carrier.FindByKey(query.CommandId);
        var result = shipment?.Status switch
        {
            ShipmentStatus.Rejected or ShipmentStatus.Cancelled => InquiryResult.Failed,
            _ when tracking is not null => InquiryResult.Succeeded,
            _ => InquiryResult.NotFound
        };
        return [new StepStatusReported(query.SagaId, query.CommandId, query.Step, result, tracking)];
    }
}
