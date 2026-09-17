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
        // Wolverine reports a conflict on the saga row as SagaConcurrencyException, not
        // EF's DbUpdateConcurrencyException; the participants' own rows raise the EF one.
        // Missing the first one dead-lettered the loser of every reply-vs-timeout race on
        // a scaled-out Orders (found by ReplyRacesTimeout_RowVersionConflict_...).
        opts.OnException(e => e is DbUpdateConcurrencyException or SagaConcurrencyException)
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

        // Cleanup of an effect that landed after its cancel (see the exception's comment).
        // Patient on purpose: the dependency is down, hammering it won't help.
        opts.OnException<DependencyUnavailableException>()
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10))
            .Then.MoveToErrorQueue();

        // "Not settled yet" resolves by itself once the provider's settle window has
        // passed, so ask again soon and often; the saga is waiting on the answer.
        // (Wolverine picks the retry slot from the envelope's total failure count, so a
        // message that already failed on a concurrency conflict starts one slot later.)
        opts.OnException<OutcomeNotSettledException>()
            .ScheduleRetry(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30))
            .Then.MoveToErrorQueue();

        // Anything else is a bug or a poison message: park it after the first failure.
        opts.OnException<Exception>().MoveToErrorQueue();
    }
}
