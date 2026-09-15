using Contracts;
using Inventory.Data;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Inventory.Handlers;

// Inventory runs its handlers in Eager transaction mode: Wolverine opens the database
// transaction before the handler runs and commits it, together with the outgoing reply,
// afterwards. Everything below is one atomic unit, and nothing in it leaves the process.
public static class ReserveStockHandler
{
    public static async Task<OutgoingMessages> Handle(ReserveStock cmd, InventoryDbContext db, TimeProvider clock, ILogger<ReserveStock> logger, CancellationToken ct)
    {
        var existing = await db.Steps.FindAsync([cmd.SagaId, StepNames.ReserveStock], ct);
        switch (existing?.Status)
        {
            case InventoryStepStatus.Reserved or InventoryStepStatus.Released:
                return [new StockReserved(cmd.SagaId, cmd.CommandId)];
            case InventoryStepStatus.Rejected:
                return [new InsufficientStock(cmd.SagaId, cmd.CommandId, existing.RejectedSku!)];
            case InventoryStepStatus.Cancelled:
                // Tombstone: the release got here first. Holding stock now would leak it.
                logger.LogWarning("Saga {SagaId}: reserve arrived after its release; rejected by tombstone", cmd.SagaId);
                return [];
        }

        var step = new InventoryStep { SagaId = cmd.SagaId, Step = StepNames.ReserveStock, UpdatedUtc = clock.GetUtcNow() };
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Inventory handlers must run in Eager transaction mode");
        await transaction.CreateSavepointAsync("reserve", ct);

        foreach (var line in cmd.Lines.OrderBy(l => l.Sku, StringComparer.Ordinal))
        {
            // The whole concurrency story for stock is this one statement: check and
            // decrement atomically, under the row lock SQL Server takes for the UPDATE.
            // No read-then-write window, so two orders can't both take the last unit, and
            // no optimistic-retry storm on a hot SKU. Lines go in SKU order so two
            // multi-line orders lock rows in the same order and can't deadlock.
            var taken = await db.Stock
                .Where(s => s.Sku == line.Sku && s.Available >= line.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Available, x => x.Available - line.Quantity), ct);

            if (taken == 0)
            {
                // All or nothing: give back the lines already taken in this transaction.
                await transaction.RollbackToSavepointAsync("reserve", ct);
                db.ChangeTracker.Clear();
                step.Status = InventoryStepStatus.Rejected;
                step.RejectedSku = line.Sku;
                db.Steps.Add(step);
                return [new InsufficientStock(cmd.SagaId, cmd.CommandId, line.Sku)];
            }

            db.Reservations.Add(new Reservation { SagaId = cmd.SagaId, OrderLineId = line.OrderLineId, Sku = line.Sku, Quantity = line.Quantity });
        }

        step.Status = InventoryStepStatus.Reserved;
        db.Steps.Add(step);
        return [new StockReserved(cmd.SagaId, cmd.CommandId)];
    }
}

public static class ReleaseStockHandler
{
    public static async Task<OutgoingMessages> Handle(ReleaseStock cmd, InventoryDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var step = await db.Steps.FindAsync([cmd.SagaId, StepNames.ReserveStock], ct);
        switch (step?.Status)
        {
            case null:
                // Release before reserve: leave a tombstone, report a no-op success.
                db.Steps.Add(new InventoryStep
                {
                    SagaId = cmd.SagaId,
                    Step = StepNames.ReserveStock,
                    Status = InventoryStepStatus.Cancelled,
                    UpdatedUtc = clock.GetUtcNow()
                });
                return [new StockReleased(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
            case InventoryStepStatus.Released:
                return [new StockReleased(cmd.SagaId, cmd.CommandId, WasNoOp: false)];
            case InventoryStepStatus.Rejected or InventoryStepStatus.Cancelled:
                return [new StockReleased(cmd.SagaId, cmd.CommandId, WasNoOp: true)];
        }

        var reservations = await db.Reservations.Where(r => r.SagaId == cmd.SagaId).OrderBy(r => r.Sku).ToListAsync(ct);
        foreach (var r in reservations)
        {
            await db.Stock.Where(s => s.Sku == r.Sku)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Available, x => x.Available + r.Quantity), ct);
        }

        db.Reservations.RemoveRange(reservations);
        step.Status = InventoryStepStatus.Released;
        step.UpdatedUtc = clock.GetUtcNow();
        return [new StockReleased(cmd.SagaId, cmd.CommandId, WasNoOp: false)];
    }
}

public static class InventoryInquiryHandler
{
    public static async Task<OutgoingMessages> Handle(CheckStepStatus query, InventoryDbContext db, CancellationToken ct)
    {
        var step = await db.Steps.AsNoTracking().FirstOrDefaultAsync(s => s.SagaId == query.SagaId && s.Step == query.Step, ct);
        var result = step?.Status switch
        {
            null => InquiryResult.NotFound,
            InventoryStepStatus.Reserved => InquiryResult.Succeeded,
            _ => InquiryResult.Failed
        };
        return [new StepStatusReported(query.SagaId, query.CommandId, query.Step, result, null)];
    }
}
