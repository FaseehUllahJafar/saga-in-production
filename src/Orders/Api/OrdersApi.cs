using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Orders.Data;
using Orders.Saga;
using ServiceDefaults;
using Wolverine;

namespace Orders.Api;

public sealed record PlaceOrderRequest(string CustomerEmail, string CardToken, string ShippingAddress, List<PlaceOrderLine> Lines);
public sealed record PlaceOrderLine(string Sku, int Quantity, decimal UnitPrice);

public static class OrdersApi
{
    public static void MapOrdersApi(this WebApplication app)
    {
        // The first idempotency layer is the client's: a checkout button double-click or a
        // retried POST after a timeout must not start a second saga and charge twice. The
        // order id is derived from the client's Idempotency-Key, so a repeat maps onto the
        // order that already exists. A repeat that overtakes the first StartCheckout is
        // absorbed by CheckoutSaga.StartOrHandle.
        app.MapPost("/orders", async (PlaceOrderRequest request, HttpContext http, OrdersDbContext db, IMessageBus bus) =>
        {
            var key = http.Request.Headers["Idempotency-Key"].ToString();
            if (key.Length is 0 or > 128)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["header required, at most 128 characters"] });
            }

            if (request.Lines is not { Count: > 0 } || request.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["at least one line with a positive quantity"] });
            }

            // Keys are scoped to the customer, the way real APIs scope them to an account.
            var scopedKey = $"{request.CustomerEmail}:{key}";
            var orderId = OrderIdFor(scopedKey);
            // The order id the client gets back is the SagaId: tag the request with it.
            Activity.Current?.SetTag(SagaTracing.TagName, orderId.ToString("D"));
            if (await db.Sagas.AnyAsync(s => s.Id == orderId))
            {
                return Results.Accepted($"/orders/{orderId}", new { orderId });
            }

            var lines = request.Lines
                .Select((l, i) => new OrderLine(OrderIdFor($"{scopedKey}:line:{i}"), l.Sku, l.Quantity, l.UnitPrice))
                .ToList();

            // Goes onto a durable local queue: persisted before the 202 is returned, so
            // an accepted order survives a crash before the saga has even started.
            await bus.SendAsync(new StartCheckout(orderId, request.CustomerEmail, request.CardToken, request.ShippingAddress, lines));
            return Results.Accepted($"/orders/{orderId}", new { orderId });
        });

        app.MapGet("/orders/{id:guid}", async (Guid id, OrdersDbContext db) =>
            await db.Sagas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id) is { } saga
                ? Results.Ok(new
                {
                    saga.Id,
                    Status = saga.Status.ToString(),
                    saga.CurrentStep,
                    saga.AttemptCount,
                    saga.Amount,
                    saga.FailureReason,
                    saga.AuthorizationId,
                    saga.TrackingNumber,
                    saga.StartedUtc,
                    saga.LastUpdatedUtc,
                    Journal = saga.Journal.Select(j => new { j.Step, Outcome = j.Outcome.ToString(), j.Reference, j.UpdatedUtc })
                })
                : Results.NotFound());

        // Operator endpoint for the SagaStuck runbook; see SagaOperations. In a real
        // deployment this sits behind operator auth, not on the public API.
        app.MapPost("/admin/sagas/{id:guid}/nudge", async (Guid id, OrdersDbContext db, IMessageBus bus) =>
            await SagaOperations.Nudge(id, db, bus) switch
            {
                NudgeResult.Sent => Results.Accepted($"/orders/{id}"),
                NudgeResult.NotInFlight => Results.Conflict(new { error = "only an InProgress or Compensating saga can be nudged" }),
                _ => Results.NotFound()
            });
    }

    private static Guid OrderIdFor(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"order:{idempotencyKey}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
