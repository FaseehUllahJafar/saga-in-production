using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;

namespace ServiceDefaults;

// The Wolverine conventions every service shares. Keeping them in one place is what
// makes "every service has a durable inbox and outbox" a fact rather than a hope.
public static class SagaMessaging
{
    public static void ApplyConventions(
        WolverineOptions opts,
        string serviceName,
        string sqlConnectionString,
        string rabbitConnectionString,
        TransactionMiddlewareMode transactionMode = TransactionMiddlewareMode.Lightweight)
    {
        opts.ServiceName = serviceName;

        // Envelope storage (inbox, outbox, scheduled messages, dead letters) lives in the
        // service's own database, so a handler's state change and the messages it emits
        // commit in ONE local transaction. That is the transactional outbox.
        opts.PersistMessagesWithSqlServer(sqlConnectionString, "wolverine");
        opts.UseEntityFrameworkCoreTransactions(transactionMode);
        opts.Policies.AutoApplyTransactions();
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.UseDurableInboxOnAllListeners();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

        opts.UseRabbitMq(new Uri(rabbitConnectionString))
            .AutoProvision()
            .UseQuorumQueues()
            // One dead-letter sink for the whole system: Wolverine's SQL table
            // (wolverine.wolverine_dead_letters), not RabbitMQ's DLX. It is queryable,
            // it is what the monitor measures, and the runbook replays from it.
            // See docs/adr/0003 and docs/runbook.md.
            .CustomizeDeadLetterQueueing(new DeadLetterQueue("unused", DeadLetterQueueMode.WolverineStorage));

        ApplyRetryOwnership(opts);
    }

    // ADR 0003: who owns which retry.
    //   Transport (here): only infrastructure blips that a quick re-run fixes.
    //   Saga (CheckoutSaga): every business-level retry, driven by its own timeouts.
    // A participant that cannot reach its dependency does NOT throw here; it simply
    // doesn't reply, and the saga's timeout decides what happens next.
    private static void ApplyRetryOwnership(WolverineOptions opts)
    {
        // Optimistic concurrency conflict on the saga row: another message for the same
        // saga committed first. Re-run from the queue so the handler reloads fresh state
        // and re-evaluates; never retry in place with a stale tracked entity.
        opts.OnException<DbUpdateConcurrencyException>()
            .ScheduleRetry(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
            .Then.MoveToErrorQueue();

        // Same idea for a duplicate command racing itself into a (SagaId, Step) primary
        // key: the loser re-runs, finds the winner's row and replays its stored outcome.
        opts.OnException<DbUpdateException>(e => e.IsUniqueViolation())
            .ScheduleRetry(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500))
            .Then.MoveToErrorQueue();

        // EF Core wraps SQL errors raised during SaveChanges, so transient ones (deadlock
        // victim, dropped connection) arrive as DbUpdateException, not SqlException.
        opts.OnException<DbUpdateException>(e => e.InnerException is SqlException { IsTransient: true })
            .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1))
            .Then.MoveToErrorQueue();

        opts.OnException<SqlException>(e => e.IsTransient)
            .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1))
            .Then.MoveToErrorQueue();

        // Anything else is a bug or a poison message: park it after the first failure.
        opts.OnException<Exception>().MoveToErrorQueue();
    }
}
