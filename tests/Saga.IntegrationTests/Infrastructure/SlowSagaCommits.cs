using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orders.Saga;

namespace Saga.IntegrationTests.Infrastructure;

// Test-only EF interceptor: when armed for a saga, holds every commit of that saga's
// row for a moment. Two messages for the same saga then load the same RowVersion and
// both try to commit, which is the race the rowversion + retry policy exists for.
// Without it the race is real but rare, and a test that relies on luck proves nothing.
public sealed class SlowSagaCommits : SaveChangesInterceptor
{
    public static readonly SlowSagaCommits Instance = new();

    private Guid? _sagaId;
    private TimeSpan _delay;
    private int _delayed;

    public int Delayed => _delayed;

    public void Arm(Guid sagaId, TimeSpan delay)
    {
        _delay = delay;
        _delayed = 0;
        _sagaId = sagaId;
    }

    public void Disarm() => _sagaId = null;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_sagaId is { } id && eventData.Context!.ChangeTracker.Entries<CheckoutSaga>().Any(e => e.Entity.Id == id))
        {
            Interlocked.Increment(ref _delayed);
            await Task.Delay(_delay, cancellationToken);
        }
        return result;
    }

}
