using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Saga.IntegrationTests.Infrastructure;

// Records the logging scopes around each log line, so a test can ask "was this line
// written with SagaId in scope?" rather than parsing message text.
// One instance per host: each host's LoggerFactory hands its provider its own scope
// stack, and a single shared instance would only ever see the last host's.
public sealed class ScopeRecorder : ILoggerProvider, ISupportExternalScope
{
    private static readonly ConcurrentQueue<(string Category, Dictionary<string, object?> Scope)> AllEntries = new();
    private IExternalScopeProvider? _scopes;

    public static IReadOnlyList<(string Category, Dictionary<string, object?> Scope)> Entries => AllEntries.ToArray();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private sealed class Recorder(ScopeRecorder owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            owner._scopes?.ForEachScope((s, acc) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    foreach (var (key, value) in pairs) acc[key] = value;
                }
            }, scope);
            AllEntries.Enqueue((category, scope));
        }
    }
}
