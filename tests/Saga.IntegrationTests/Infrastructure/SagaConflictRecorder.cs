using Microsoft.Extensions.Logging;
using Wolverine;

namespace Saga.IntegrationTests.Infrastructure;

// Counts saga optimistic-concurrency conflicts as Wolverine reports them. Wolverine
// detects the conflict itself and raises SagaConcurrencyException from the generated
// handler, so no EF hook sees it; its log event is the one reliable signal.
public sealed class SagaConflictRecorder : ILoggerProvider
{
    public static readonly SagaConflictRecorder Instance = new();

    private int _conflicts;

    public int Conflicts => _conflicts;

    public void Reset() => Interlocked.Exchange(ref _conflicts, 0);

    public ILogger CreateLogger(string categoryName) => new Recorder(this);

    public void Dispose() { }

    private sealed class Recorder(SagaConflictRecorder owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is SagaConcurrencyException || exception?.InnerException is SagaConcurrencyException)
            {
                Interlocked.Increment(ref owner._conflicts);
            }
        }
    }
}
