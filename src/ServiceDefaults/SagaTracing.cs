using System.Diagnostics;
using Contracts;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace ServiceDefaults;

// One SagaId, one question: "what happened to this order?" It is put on every span as
// saga.id and on every log line written while handling the message as the SagaId scope,
// in every service and in FakePay (via the X-Saga-Id header). Filter the Aspire dashboard
// on saga.id and one order's whole story lines up, across processes, including the
// provider's side of each HTTP call.
public static class SagaTracing
{
    public const string TagName = "saga.id";
    public const string ScopeKey = "SagaId";
    public const string Header = "X-Saga-Id";

    public static IDisposable? Begin(ILogger logger, Guid sagaId)
    {
        var id = sagaId.ToString("D");
        Activity.Current?.SetTag(TagName, id);
        return logger.BeginScope(new Dictionary<string, object> { [ScopeKey] = id });
    }
}

// Wolverine middleware for every handler of an ISagaMessage (SagaMessaging applies it).
// Runs inside Wolverine's own handler span, so the tag lands on the span that shows the
// handler's duration and, if it throws, its exception.
//
// The scope covers what the handler logs, EF and HttpClient included. Wolverine logs a
// failure after the handler has returned, outside it; the tagged span carries that one.
public static class SagaIdMiddleware
{
    // Takes the Envelope, not ISagaMessage: Wolverine's code generation binds a handler's
    // message by its concrete type only.
    public static SagaLogScope Before(Envelope envelope, ILoggerFactory loggers) =>
        new(envelope.Message is ISagaMessage message ? SagaTracing.Begin(loggers.CreateLogger("Saga"), message.SagaId) : null);

    public static void Finally(SagaLogScope scope) => scope.Dispose();
}

public sealed class SagaLogScope(IDisposable? inner) : IDisposable
{
    public void Dispose() => inner?.Dispose();
}
