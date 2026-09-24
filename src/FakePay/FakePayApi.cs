using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FakePay.Data;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;

namespace FakePay;

// Amounts are integer minor units (cents), as at Stripe. A decimal on the wire is not
// one value: 25 and 25.00 are equal but serialize differently, so a retry built from a
// reloaded saga (SQL hands back decimal(18,2)) hashed differently from the original
// request and was refused as "key reused with a different request". The lost-response
// integration test found that.
public sealed record AuthorizeRequest(long AmountMinor, string CardToken, string Currency = "USD");
public sealed record CaptureRequest(long AmountMinor);
public sealed record AuthorizationResponse(string Id, string Status, decimal Amount, string? CaptureId);
public sealed record ErrorResponse(string Error);

// A stand-in for an external card processor, with its own database, so the saga meets
// the same things it would meet in production: idempotency keys, declines, expiring
// authorizations, flaky captures and responses that never arrive.
//
// Magic card tokens drive the scenarios:
//   tok_visa           happy path
//   tok_decline        402 card_declined
//   tok_expired_auth   authorization expires at once, so capture fails terminally
//   tok_flaky_capture  first capture attempt returns 503, later ones succeed
//   tok_slow           authorize commits, then answers late (past the caller's timeout)
//   tok_slow_commit    authorize waits BEFORE committing, so a void can overtake it
//   tok_capture_down   every capture answers 503 and records nothing
public static class FakePayApi
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromDays(7);
    private static readonly HashSet<string> SupportedCurrencies = ["USD", "EUR", "GBP"];

    // The idempotency hash must not change for a request that means the same thing.
    // Adding Currency changed the request's serialized form, so a retry sent after the
    // deploy, of an authorize first sent before it, would hash differently and be refused
    // as "key reused with a different request". A USD request therefore hashes in its
    // original two-field shape; only another currency adds the field.
    private static object HashShape(AuthorizeRequest request) =>
        request.Currency == "USD" ? new { request.AmountMinor, request.CardToken } : request;

    public static void MapFakePayApi(this WebApplication app)
    {
        // The caller's SagaId, on FakePay's side of the call: its request span and its
        // log lines line up with the saga that caused them.
        app.Use(async (http, next) =>
        {
            using var scope = Guid.TryParse(http.Request.Headers[SagaTracing.Header], out var sagaId)
                ? SagaTracing.Begin(app.Logger, sagaId)
                : null;
            await next(http);
        });

        app.MapPost("/v1/authorizations", async (AuthorizeRequest request, HttpContext http, FakePayDbContext db, TimeProvider clock, FakePayOptions options) =>
        {
            if (!TryGetKey(http, out var key)) return MissingKey();
            request = request with { Currency = (request.Currency ?? "USD").ToUpperInvariant() };

            if (request.CardToken == "tok_slow_commit")
            {
                // Not cancellable on purpose: a real processor finishes the charge even
                // after the caller hung up.
                await Task.Delay(options.SlowResponseDelay);
            }

            var result = await Idempotent(db, clock, key, "authorize", HashShape(request), async () =>
            {
                if (request.CardToken == "tok_decline")
                {
                    return (402, new ErrorResponse("card_declined"), null);
                }

                if (!SupportedCurrencies.Contains(request.Currency))
                {
                    return (400, new ErrorResponse("unsupported_currency"), null);
                }

                var now = clock.GetUtcNow();
                var authorization = new Authorization
                {
                    Id = $"auth_{Guid.CreateVersion7():N}",
                    Amount = request.AmountMinor / 100m,
                    Currency = request.Currency,
                    Status = AuthorizationStatus.Authorized,
                    CreatedUtc = now,
                    ExpiresUtc = request.CardToken == "tok_expired_auth" ? now : now + AuthorizationLifetime,
                    // Remembered on the row so the flaky-capture scenario survives restarts.
                    CaptureAttempts = request.CardToken switch
                    {
                        "tok_flaky_capture" => -1,
                        "tok_capture_down" => int.MinValue,
                        _ => 0
                    },
                };
                db.Authorizations.Add(authorization);
                return (201, ToResponse(authorization), authorization.Id);
            });

            if (request.CardToken == "tok_slow")
            {
                // The ledger row is already committed. The caller times out and never sees
                // this response: the "effect happened, answer lost" case.
                await Task.Delay(options.SlowResponseDelay, http.RequestAborted).ContinueWith(_ => { });
            }

            return result;
        });

        app.MapPost("/v1/authorizations/{id}/capture", async (string id, CaptureRequest request, HttpContext http, FakePayDbContext db, TimeProvider clock) =>
        {
            if (!TryGetKey(http, out var key)) return MissingKey();

            var authorization = await db.Authorizations.FindAsync(id);
            if (authorization is null) return Results.NotFound(new ErrorResponse("no_such_authorization"));

            if (authorization.CaptureAttempts == int.MinValue)
            {
                return Results.Json(new ErrorResponse("processor_unavailable"), statusCode: 503);
            }

            if (authorization.CaptureAttempts < 0)
            {
                authorization.CaptureAttempts = 0;
                await db.SaveChangesAsync();
                return Results.Json(new ErrorResponse("processor_unavailable"), statusCode: 503);
            }

            return await Idempotent(db, clock, key, "capture", request, () =>
            {
                (int, object, string?) outcome = authorization switch
                {
                    { Status: AuthorizationStatus.Voided } => (409, new ErrorResponse("authorization_voided"), id),
                    { Status: AuthorizationStatus.Captured } => (409, new ErrorResponse("already_captured"), id),
                    _ when authorization.ExpiresUtc <= clock.GetUtcNow() => (409, new ErrorResponse("authorization_expired"), id),
                    _ when request.AmountMinor / 100m > authorization.Amount => (422, new ErrorResponse("amount_exceeds_authorization"), id),
                    _ => Capture(authorization),
                };
                return Task.FromResult(outcome);
            });
        });

        // Naturally idempotent: voiding a voided authorization is a 200.
        app.MapPost("/v1/authorizations/{id}/void", async (string id, FakePayDbContext db) =>
        {
            var authorization = await db.Authorizations.FindAsync(id);
            if (authorization is null) return Results.NotFound(new ErrorResponse("no_such_authorization"));
            if (authorization.Status == AuthorizationStatus.Captured) return Results.Conflict(new ErrorResponse("already_captured"));

            authorization.Status = AuthorizationStatus.Voided;
            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(authorization));
        });

        // The inquiry endpoint: "what happened to the request I sent with this key?"
        app.MapGet("/v1/lookup/{operation}/{key}", async (string operation, string key, FakePayDbContext db) =>
        {
            var record = await db.IdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Key == key && r.Operation == operation);
            // Key expiry only limits REPLAY. Looking up what happened must keep working
            // for as long as the authorization itself can be live, or a late void would
            // be told "never heard of it" about a hold that still exists.
            if (record is null)
            {
                return Results.NotFound(new ErrorResponse("no_such_request"));
            }

            // A request that was refused (declined card) still happened: report the stored
            // refusal, not "never heard of it".
            if (record.AuthorizationId is null)
            {
                return Results.Content(record.ResponseJson, "application/json", Encoding.UTF8, record.StatusCode);
            }

            var authorization = await db.Authorizations.AsNoTracking().FirstAsync(a => a.Id == record.AuthorizationId);
            return record.StatusCode is >= 200 and < 300
                ? Results.Ok(ToResponse(authorization))
                : Results.Json(JsonDocument.Parse(record.ResponseJson).RootElement, statusCode: record.StatusCode);
        });
    }

    private static (int, object, string?) Capture(Authorization authorization)
    {
        authorization.Status = AuthorizationStatus.Captured;
        authorization.CaptureId = $"cap_{Guid.CreateVersion7():N}";
        return (200, ToResponse(authorization), authorization.Id);
    }

    private static async Task<IResult> Idempotent(
        FakePayDbContext db,
        TimeProvider clock,
        string key,
        string operation,
        object request,
        Func<Task<(int Status, object Body, string? AuthorizationId)>> execute)
    {
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        var now = clock.GetUtcNow();

        var existing = await db.IdempotencyKeys.FirstOrDefaultAsync(r => r.Key == key && r.Operation == operation);
        if (existing is not null && existing.CreatedUtc + KeyLifetime < now)
        {
            db.IdempotencyKeys.Remove(existing);
            existing = null;
        }

        if (existing is not null)
        {
            return await Replay(db, existing, requestHash);
        }

        var (status, body, authorizationId) = await execute();
        var json = JsonSerializer.Serialize(body, body.GetType());

        db.IdempotencyKeys.Add(new IdempotencyRecord
        {
            Key = key,
            Operation = operation,
            RequestHash = requestHash,
            StatusCode = status,
            ResponseJson = json,
            AuthorizationId = authorizationId,
            CreatedUtc = now,
        });

        try
        {
            // The ledger change and the idempotency record commit together, so a retry
            // can never see the effect without the stored response, or the reverse.
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            // A concurrent request with the same key won. Answer with what it stored.
            db.ChangeTracker.Clear();
            var winner = await db.IdempotencyKeys.AsNoTracking().FirstAsync(r => r.Key == key && r.Operation == operation);
            return await Replay(db, winner, requestHash);
        }

        return Results.Content(json, "application/json", Encoding.UTF8, status);
    }

    private static async Task<IResult> Replay(FakePayDbContext db, IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash != requestHash)
        {
            return Results.Conflict(new ErrorResponse("idempotency_key_reused_with_different_request"));
        }

        await db.IdempotencyKeys
            .Where(r => r.Key == record.Key && r.Operation == record.Operation)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReplayCount, r => r.ReplayCount + 1));
        return Results.Content(record.ResponseJson, "application/json", Encoding.UTF8, record.StatusCode);
    }

    private static bool TryGetKey(HttpContext http, out string key)
    {
        key = http.Request.Headers["Idempotency-Key"].ToString();
        return key.Length is > 0 and <= 64;
    }

    private static IResult MissingKey() => Results.BadRequest(new ErrorResponse("idempotency_key_required"));

    private static AuthorizationResponse ToResponse(Authorization a) =>
        new(a.Id, a.Status.ToString().ToLowerInvariant(), a.Amount, a.CaptureId);
}
