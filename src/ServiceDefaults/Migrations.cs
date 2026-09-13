using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ServiceDefaults;

public static class DatabaseMigrations
{
    // Runs before the host starts, so no listener picks up a message before its tables
    // exist. Fine for a demo with one replica per service; production would run
    // migrations as a separate deploy step, not on every node's startup.
    public static async Task MigrateDatabaseAsync<TContext>(this IHost host) where TContext : DbContext
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync();
    }
}
