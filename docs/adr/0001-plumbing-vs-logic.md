# 0001: Wolverine does the plumbing, the saga logic is ours

Status: accepted (after the Phase 0 spike)

## Context

A checkout saga needs two kinds of code:

- **Plumbing.** This means correlating a reply to its saga row, optimistic concurrency on that row, a transactional outbox, a durable inbox, and timeouts that survive a process restart.
- **Logic.** This means which step comes next, what a timeout means, what gets compensated and in what order, and when to give up.

Hand-rolling the plumbing is weeks of work that every messaging framework already does. It is also the first thing a reviewer would question. Hiding the logic inside a framework's state-machine DSL has the opposite problem: the part worth reading disappears.

## Decision

Wolverine (MIT) owns the plumbing:

- RabbitMQ transport with quorum queues.
- SQL Server envelope storage per service, which gives each service an inbox, an outbox, scheduled messages and dead letters.
- EF Core transactional middleware, and saga persistence.

We own the logic. `CheckoutSaga` is a plain class: the transition table, the compensation journal, direction-aware timeout gating and every compensation are written by hand in `src/Orders/Saga`. Wolverine persists it as **our own EF Core entity**, so every column (Status, CurrentStep, AttemptCount, LastUpdatedUtc, Journal, and so on) is an ordinary SQL column that the monitor and the runbook query directly.

## What the spike found

- **EF-mapped saga state works.** Wolverine loads and saves `CheckoutSaga` through `OrdersDbContext`. The saga row and its outgoing messages commit in one transaction. The rowversion column gives optimistic concurrency. The fallback (our own orchestrator on plain handlers) was not needed.
- **Wolverine 6 no longer ships the runtime compiler.** Handler code generation needs `WolverineFx.RuntimeCompilation`, otherwise the host fails at startup. We use runtime compilation for now. Pre-generating code (`codegen write` plus `TypeLoadMode.Static`) is the production step and is listed under Limits.
- **Typed `HttpClient`s trip code generation.** A typed client is registered through a factory lambda, so Wolverine falls back to lazy service location, and the generated handler failed to arrange its frames. `FakePayClient` is therefore a singleton over `IHttpClientFactory`, which Wolverine can construct inline.
- **Dead letters go to one kind of sink.** RabbitMQ dead-lettering is set to `WolverineStorage`, so poison messages land in each service's own `wolverine.wolverine_dead_letters` table and never in a broker DLX. That is one table per service with the same schema everywhere, so there is a single query, alert and replay procedure. The broker DLX would have been a second mechanism. The first run of the stack proved the path: a handler failure moved the envelope there.
- **Persistent containers and generated passwords don't mix.** A persistent SQL container with a data volume kept the old `sa` password while Aspire generated a new one, so every service failed to log in. The AppHost now uses session-lifetime containers with no volume. Every `aspire run` starts from empty databases and the services migrate on startup.
- **The Aspire 13 AppHost expects the Aspire CLI** (`AspireUseCliBundle=true`, `aspire run`).

## Consequences

- The Orders project reads as the saga, not as framework glue.
- We depend on Wolverine's saga and outbox semantics. They are pinned (6.40.0) and covered by the integration tests. A Wolverine upgrade runs those tests before it merges.
- MassTransit v9 and NServiceBus were not chosen because both are commercial. MassTransit v8 is still Apache-licensed, but its support window ends.
