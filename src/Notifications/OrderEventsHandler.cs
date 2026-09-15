using Contracts;
using Microsoft.EntityFrameworkCore;
using Notifications.Data;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Notifications;

// Outside the saga on purpose. The order is complete whether or not the email goes
// out; a failing email provider gets its own retries, its own dead letters and its own
// alert, and never reaches back into the checkout.
public static class OrderEventsHandler
{
    public static void Configure(HandlerChain chain)
    {
        // Chain-level rules win over the global ones in SagaMessaging: an email outage
        // is worth waiting out for several minutes before parking the message.
        chain.OnException<EmailProviderException>()
            .ScheduleRetry(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10))
            .Then.MoveToErrorQueue();
    }

    public static Task Handle(OrderCompleted e, NotificationsDbContext db, EmailSender email, NotificationMetrics metrics, TimeProvider clock, CancellationToken ct) =>
        SendOnce(e.OrderId, "completed", e.CustomerEmail, $"Order {e.OrderId} confirmed ({e.Amount:0.00})", db, email, metrics, clock, ct);

    public static Task Handle(OrderCancelled e, NotificationsDbContext db, EmailSender email, NotificationMetrics metrics, TimeProvider clock, CancellationToken ct) =>
        SendOnce(e.OrderId, "cancelled", e.CustomerEmail, $"Order {e.OrderId} could not be completed", db, email, metrics, clock, ct);

    private static async Task SendOnce(
        Guid orderId, string kind, string to, string subject,
        NotificationsDbContext db, EmailSender email, NotificationMetrics metrics, TimeProvider clock, CancellationToken ct)
    {
        // At-least-once on purpose: two copies racing here can both send, and the
        // customer gets the same email twice. Recording before sending would flip that to
        // at-most-once, and a lost confirmation email is the worse failure.
        if (await db.Sent.AnyAsync(s => s.OrderId == orderId && s.Kind == kind, ct))
        {
            return;
        }

        try
        {
            await email.SendAsync(to, subject, ct);
        }
        catch (EmailProviderException)
        {
            metrics.Failed(kind);
            throw;
        }

        metrics.Sent(kind);
        db.Sent.Add(new SentNotification { OrderId = orderId, Kind = kind, SentUtc = clock.GetUtcNow() });
    }
}
