using Contracts;
using Inventory.Data;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.RabbitMQ;

namespace Inventory;

public static class InventorySetup
{
    public const string DatabaseName = "inventory-db";

    public static IHostApplicationBuilder AddInventoryService(this IHostApplicationBuilder builder)
    {
        var sql = builder.Configuration.GetConnectionString(DatabaseName)
            ?? throw new InvalidOperationException($"Connection string '{DatabaseName}' is missing");
        var rabbit = builder.Configuration.GetConnectionString("messaging")
            ?? throw new InvalidOperationException("Connection string 'messaging' is missing");

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContextWithWolverineIntegration<InventoryDbContext>(o => o.UseSqlServer(sql));

        builder.UseWolverine(opts =>
        {
            // Eager: every handler here is pure database work, so one transaction around
            // the whole handler (stock, reservations, step row and reply) is what we want.
            SagaMessaging.ApplyConventions(opts, "inventory", sql, rabbit, TransactionMiddlewareMode.Eager);
            // Handler discovery from this assembly, not the entry assembly: under test every
            // service runs in one process whose entry assembly is the test project.
            opts.ApplicationAssembly = typeof(InventorySetup).Assembly;

            opts.ListenToRabbitQueue(Queues.Inventory);
            opts.PublishAllMessages().ToRabbitQueue(Queues.Orders);
        });

        return builder;
    }
}
