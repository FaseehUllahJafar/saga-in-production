# 0004: How a message changes shape

Status: accepted

## Context

A saga's messages outlive the code that wrote them. At the moment a deploy starts, there are commands in RabbitMQ queues, rows in every service's outbox, `StartCheckout`s in Orders' durable local queue, scheduled timeouts days ahead, and dead letters that someone will replay next week. All of them were serialized by the previous release. Some will be read by the next one. During a rolling deploy, old and new versions of a service also run side by side, both reading the same queue.

So "change the record and redeploy" is never atomic. The first real change was adding `Currency` to `AuthorizePayment`, which until then was implicitly dollars.

## Decision

**Only additive changes, with a default that says what the old message meant.** `AuthorizePayment` gained `string Currency = "USD"`. A message without the field is not "currency unknown". It was written by a release that only knew dollars, so it *is* dollars. The same default sits on `StartCheckout` (still queued at deploy time) and on the saga table's new column (sagas in flight at deploy time).

**Readers deploy first.** An older Payments ignores a property it doesn't know: System.Text.Json skips unknown members. So an old reader would not fail on a EUR command. It would authorize it silently as dollars. That is worse than failing. The order is:

1. Payments and FakePay learn `Currency`, and default it when it's missing.
2. Orders starts sending it: the saga carries it, and the API accepts `currency` on `POST /orders`.
3. Only after that does anything send a currency other than USD.

Nothing enforces step 3 here. In a real team it would sit behind a flag that is switched on only after every Payments instance reports the new version.

**The idempotency hash must not move.** FakePay hashes each request to catch "same key, different request". Adding a field changed the serialized request. An authorize sent before the deploy and retried after it (same key, same meaning) would then have been refused with 409, and a charge that had landed would have looked like a failure. A USD request is therefore hashed in its original shape. A different currency is a different request, and it still gets 409.

**Removing or renaming is two releases.** Stop reading the field, wait out every queue, outbox, schedule and dead letter that could hold the old shape, then stop writing it. The longest of those here is a scheduled timeout, a few minutes, but a dead letter can wait days, so the wait is "until the dead-letter tables hold none". This repo hasn't had to do it yet.

**Wire names are stable.** Wolverine routes on the message type name (`Contracts.AuthorizePayment`). Moving or renaming the C# type without pinning the old name with `[MessageIdentity]` would strand every in-flight message. The test uses exactly that attribute to send an old-shape message under today's name.

## Consequences

- A review claimed System.Text.Json ignores a constructor parameter's default and binds a missing property as null. On .NET 10 it doesn't: the old-shape test would get a 400 from FakePay, and the retry test a 409, if it did.
- `MessageEvolutionTests` covers the change three ways: an old-shape `AuthorizePayment` on the real broker is authorized in dollars; a EUR order completes in euros end to end; and a same-key retry across the deploy replays instead of getting 409.
- `OrderCompleted` gained `Currency` the same way, in the same change: a review pointed out that a EUR order's confirmation email would otherwise print a bare number. `CapturePayment` still carries an amount without one. That is safe, because a capture can only take the authorization it names, and the authorization has a currency.
- Defaults are forever. Once a message without `Currency` means USD, that meaning can't change while any such message might still exist.
