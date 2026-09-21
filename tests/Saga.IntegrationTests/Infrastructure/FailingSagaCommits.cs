using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orders.Saga;

namespace Saga.IntegrationTests.Infrastructure;

// Test-only EF interceptor: when armed for a saga, every commit of that saga's row throws
// a non-transient exception, the way a bad deploy or a schema mismatch would. The retry
// policy treats it as a bug and dead-letters the message on the first failure.
public sealed class FailingSagaCommits : SaveChangesInterceptor
{
    public static readonly FailingSagaCommits Instance = new();

    private Guid? _sagaId;

    public void Arm(Guid sagaId) => _sagaId = sagaId;

    public void Disarm() => _sagaId = null;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_sagaId is { } id && eventData.Context!.ChangeTracker.Entries<CheckoutSaga>().Any(e => e.Entity.Id == id))
        {
            throw new InvalidOperationException($"test: commit of saga {id} refused");
        }
        return ValueTask.FromResult(result);
    }
}
